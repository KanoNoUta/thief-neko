using System.Diagnostics;
using CatapiController;
using static CatapiController.Tests.AuthTestSupport;

namespace CatapiController.Tests;

internal static class CatpawAuthServiceTests
{
    private const string Tenant = "catpaw-test-tenant";

    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Catpaw auth service coalesces refresh and persists rotated tokens",
            ConcurrentRefreshPersistsBothTokensAsync);
        yield return ("Catpaw auth service retries transient failures and retains session",
            TransientFailureRetriesAndRetainsSessionAsync);
        yield return ("Catpaw auth service marks rejected sessions login required",
            AuthRejectionMarksLoginRequiredAsync);
        yield return ("Catpaw auth service imports and validates desktop session",
            ImportDesktopSessionAsync);
        yield return ("Catpaw auth service redacts desktop import failures",
            ImportFailureIsRedactedAsync);
        yield return ("Catpaw auth service terminates timed out desktop helper",
            TimedOutImportTerminatesHelperAsync);
        yield return ("Catpaw auth service schedules before expiry without overlap",
            SchedulesBeforeExpiryWithoutOverlapAsync);
        yield return ("Catpaw auth service uses conservative schedule without expiry",
            SchedulesConservativelyWithoutExpiryAsync);
    }

    private static async Task ConcurrentRefreshPersistsBothTokensAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeAuthClient
        {
            Refresh = async (_, ct) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return Session("rotated-access", "rotated-refresh");
            },
        };
        await using var service = Service(client, store);
        await service.SaveLoginAsync(Session("old-access", "old-refresh"), default);

        var first = service.RefreshAsync(true, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = service.RefreshAsync(true, default);
        release.SetResult();

        var results = await Task.WhenAll(first, second);
        AssertEqual(1, client.RefreshCalls, "concurrent refresh should invoke client once");
        AssertEqual("rotated-access", results[0].AccessToken,
            "all callers should receive rotated access token");
        AssertEqual("rotated-access", results[1].AccessToken,
            "coalesced caller should receive rotated access token");
        var persisted = await store.LoadAsync();
        AssertTrue(persisted is not null, "refreshed session should be persisted");
        AssertEqual("rotated-access", persisted!.AccessToken,
            "rotated access token should be persisted");
        AssertEqual("rotated-refresh", persisted.RefreshToken,
            "rotated refresh token should be persisted");
    }

    private static async Task TransientFailureRetriesAndRetainsSessionAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var delays = new List<TimeSpan>();
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => throw new CatpawAuthException(
                "redacted transient failure", CatpawAuthFailureKind.Transient),
        };
        await using var service = Service(client, store,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });
        var original = Session("retained-access", "retained-refresh");
        await service.SaveLoginAsync(original, default);

        await AssertThrowsAsync<CatpawAuthException>(
            () => service.RefreshAsync(true, default),
            "exhausted transient refresh should fail");

        AssertEqual(4, client.RefreshCalls, "refresh should make one attempt and three retries");
        AssertSequenceEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) },
            delays,
            "refresh should use bounded retry delays");
        var retained = await service.GetSessionAsync(default);
        AssertEqual(original.AccessToken, retained!.AccessToken,
            "failed refresh should retain access token");
        AssertEqual(original.RefreshToken, retained.RefreshToken,
            "failed refresh should retain refresh token");
    }

    private static async Task AuthRejectionMarksLoginRequiredAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var delays = 0;
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => throw new CatpawAuthException(
                "redacted rejection", CatpawAuthFailureKind.AuthRejected),
        };
        await using var service = Service(client, store,
            delay: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });
        await service.SaveLoginAsync(Session("access-secret", "refresh-secret"), default);

        await AssertThrowsAsync<CatpawAuthException>(
            () => service.RefreshAsync(true, default),
            "authentication rejection should fail refresh");

        AssertEqual(1, client.RefreshCalls, "authentication rejection should not be retried");
        AssertEqual(0, delays, "authentication rejection should not be delayed");
        var status = service.GetStatus();
        AssertTrue(!status.SignedIn, "rejected session should not report signed in");
        AssertEqual("LoginRequired", status.State, "rejected session should require login");
        var retained = await store.LoadAsync();
        AssertEqual("refresh-secret", retained!.RefreshToken,
            "authentication rejection must retain stored refresh token");
    }

    private static async Task ImportDesktopSessionAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        string? requestedPath = null;
        var client = new FakeAuthClient
        {
            UserInfo = (token, _) =>
            {
                AssertEqual("desktop-access", token,
                    "desktop access token should be validated");
                return Task.FromResult(new AccountInfo("desktop-user", "Desktop Account"));
            },
        };
        await using var service = Service(client, store,
            desktopStateReader: (path, _) =>
            {
                requestedPath = path;
                return Task.FromResult(
                    "{\"token\":\"desktop-access\",\"refreshToken\":\"desktop-refresh\",\"userMis\":\"desktop-user\",\"accountLabel\":\"Desktop Account\"}");
            });

        var imported = await service.ImportDesktopSessionAsync(directory.Path, default);

        AssertEqual(directory.Path, requestedPath, "import should execute in gateway path");
        AssertEqual(1, client.UserInfoCalls, "import should validate user info once");
        AssertEqual("desktop-refresh", imported.RefreshToken,
            "import should retain desktop refresh token");
        AssertEqual("Desktop Account", imported.AccountLabel,
            "import should retain desktop account label");
        var persisted = await store.LoadAsync();
        AssertEqual(imported, persisted, "validated desktop session should be persisted");
    }

    private static async Task ImportFailureIsRedactedAsync()
    {
        const string secret = "credential-from-child-output";
        using var directory = new AuthTemporaryDirectory();
        await using var service = Service(
            new FakeAuthClient(),
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            desktopStateReader: (_, _) => throw new InvalidOperationException(secret));

        var error = await AssertThrowsAsync<InvalidOperationException>(
            () => service.ImportDesktopSessionAsync(directory.Path, default),
            "failed desktop helper should be reported");

        AssertTrue(!error.ToString().Contains(secret, StringComparison.Ordinal),
            "desktop helper credentials must not appear in errors");
    }

    private static async Task TimedOutImportTerminatesHelperAsync()
    {
        const string secret = "credential-from-timed-out-child";
        using var directory = new AuthTemporaryDirectory();
        var sourceDirectory = Path.Combine(directory.Path, "src");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "catpawState.js"),
            $"import fs from 'node:fs'; fs.writeFileSync('reader.pid', String(process.pid)); console.error('{secret}'); setInterval(() => {{}}, 1000);");
        await using var service = Service(
            new FakeAuthClient(),
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")));

        var error = await AssertThrowsAsync<InvalidOperationException>(
            () => service.ImportDesktopSessionAsync(directory.Path, default),
            "timed out desktop helper should fail import");

        AssertTrue(!error.ToString().Contains(secret, StringComparison.Ordinal),
            "timed out helper output must not appear in errors");
        var processId = int.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "reader.pid")));
        await Task.Delay(100);
        var stillRunning = IsProcessRunning(processId);
        if (stillRunning)
        {
            Process.GetProcessById(processId).Kill(true);
        }
        AssertTrue(!stillRunning,
            "timed out desktop helper should be terminated");
    }

    private static async Task SchedulesBeforeExpiryWithoutOverlapAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var now = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var scheduled = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeAuthClient
        {
            Refresh = async (_, ct) =>
            {
                refreshEntered.TrySetResult();
                await releaseRefresh.Task.WaitAsync(ct);
                return Session("scheduled-access", "scheduled-refresh") with
                {
                    AccessExpiresAt = now.AddHours(1),
                };
            },
        };
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: async (duration, ct) =>
            {
                scheduled.TrySetResult(duration);
                await releaseDelay.Task.WaitAsync(ct);
            },
            timeProvider: new FixedTimeProvider(now));
        await service.SaveLoginAsync(Session("access", "refresh") with
        {
            AccessExpiresAt = now.AddMinutes(20),
        }, default);

        await service.StartAsync(default);
        AssertEqual(TimeSpan.FromMinutes(15),
            await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            "refresh should be scheduled five minutes before expiry");
        releaseDelay.SetResult();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var concurrent = service.RefreshAsync(true, default);
        await Task.Delay(20);
        AssertEqual(1, client.RefreshCalls, "scheduled and requested refresh must not overlap");
        releaseRefresh.SetResult();
        await concurrent;
        await service.StopAsync(default);
    }

    private static async Task SchedulesConservativelyWithoutExpiryAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var scheduled = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(
            new FakeAuthClient(),
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: (duration, ct) =>
            {
                scheduled.TrySetResult(duration);
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        await service.SaveLoginAsync(Session("access", "refresh"), default);

        await service.StartAsync(default);
        AssertEqual(TimeSpan.FromMinutes(45),
            await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            "missing expiry should use conservative refresh interval");
        await service.StopAsync(default);
    }

    private static CatpawAuthService Service(
        ICatpawAuthClient client,
        AuthSessionStore store,
        Func<string, CancellationToken, Task<string>>? desktopStateReader = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null) => new(
            client, store, Tenant, desktopStateReader, delay, timeProvider);

    private static AuthSession Session(string accessToken, string refreshToken) => new(
        accessToken,
        refreshToken,
        "user-1",
        "Test Account",
        Tenant,
        null,
        DateTimeOffset.UtcNow.AddDays(30),
        DateTimeOffset.UtcNow);

    private static async Task<T> AssertThrowsAsync<T>(Func<Task> action, string message)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertSequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class FakeAuthClient : ICatpawAuthClient
    {
        public Func<AuthSession, CancellationToken, Task<AuthSession>> Refresh { get; init; } =
            (session, _) => Task.FromResult(session);
        public Func<string, CancellationToken, Task<AccountInfo>> UserInfo { get; init; } =
            (_, _) => Task.FromResult(new AccountInfo("user-1", "Test Account"));
        public int RefreshCalls { get; private set; }
        public int UserInfoCalls { get; private set; }

        public Task<AuthSession> RefreshAsync(AuthSession current, CancellationToken ct)
        {
            RefreshCalls++;
            return Refresh(current, ct);
        }

        public Task<AccountInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)
        {
            UserInfoCalls++;
            return UserInfo(accessToken, ct);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
