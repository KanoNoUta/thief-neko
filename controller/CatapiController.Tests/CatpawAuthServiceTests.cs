using System.Diagnostics;
using System.Text.Json;
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
        yield return ("Catpaw auth service backs off after scheduled retries are exhausted",
            ScheduledFailureUsesRetryFloorAsync);
        yield return ("Catpaw auth service escalates concurrent no-op to forced refresh",
            ForcedRefreshEscalatesConcurrentNoOpAsync);
        yield return ("Catpaw auth service forced caller joins concurrent real refresh",
            ForcedRefreshJoinsConcurrentRealRefreshAsync);
        yield return ("Catpaw auth service persists refresh before publication",
            RefreshPersistsBeforePublicationAsync);
        yield return ("Catpaw auth service status and errors redact credentials",
            StatusAndErrorsRedactCredentialsAsync);
        yield return ("Catpaw auth service newer login discards stale refresh response",
            NewerLoginDiscardsStaleRefreshAsync);
        yield return ("Catpaw auth service serializes refresh and newer login persistence",
            RefreshAndLoginPersistenceIsSerializedAsync);
        yield return ("Catpaw auth service newer login discards stale auth rejection",
            NewerLoginDiscardsStaleAuthRejectionAsync);
        yield return ("Catpaw auth service newer login cancels stale retry delay",
            NewerLoginPreventsStaleRetryAsync);
        yield return ("Catpaw auth service login wins auth failure status race",
            LoginWinsAuthFailureStatusRaceAsync);
        yield return ("Catpaw auth service login wins transient failure status race",
            LoginWinsTransientFailureStatusRaceAsync);
        yield return ("Catpaw auth scheduler stays alive while signed out",
            SchedulerWakesWhenLoginIsSavedAsync);
        yield return ("Catpaw auth scheduler wakes for earlier replacement expiry",
            SchedulerWakesForEarlierExpiryAsync);
        yield return ("Catpaw auth refresh rejects pre-cancelled caller before work",
            RefreshRejectsPreCancelledCallerAsync);
        yield return ("Catpaw auth cancelled stop retains scheduler ownership",
            CancelledStopRetainsSchedulerOwnershipAsync);
        yield return ("Catpaw auth disposal awaits active refresh cleanup",
            DisposalAwaitsActiveRefreshAsync);
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
                return Task.FromResult(new AccountInfo("desktop-user", "Authoritative Account"));
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
        AssertEqual("Authoritative Account", imported.AccountLabel,
            "import should persist authoritative account label from user info");
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

    private static async Task ScheduledFailureUsesRetryFloorAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var delays = new List<TimeSpan>();
        var failureFloor = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => throw new CatpawAuthException(
                "redacted transient failure", CatpawAuthFailureKind.Transient),
        };
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: (duration, ct) =>
            {
                lock (delays)
                {
                    delays.Add(duration);
                    if (delays.Count == 5)
                    {
                        failureFloor.TrySetResult(duration);
                        return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                }

                return Task.CompletedTask;
            },
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-07-11T10:00:00Z")));
        await service.SaveLoginAsync(Session("expired-access", "retained-refresh") with
        {
            AccessExpiresAt = DateTimeOffset.Parse("2026-07-11T09:00:00Z"),
        }, default);

        await service.StartAsync(default);

        AssertEqual(TimeSpan.FromMinutes(5),
            await failureFloor.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            "exhausted scheduled refresh should apply a five-minute retry floor");
        AssertEqual(4, client.RefreshCalls,
            "scheduler should stop issuing refreshes while failure floor is pending");
        lock (delays)
        {
            AssertSequenceEqual(
                new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMinutes(5),
                },
                delays,
                "scheduler delay sequence should be bounded after transient exhaustion");
        }
        await service.StopAsync(default);
    }

    private static async Task ForcedRefreshEscalatesConcurrentNoOpAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var loadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Session("current-access", "current-refresh") with
        {
            AccessExpiresAt = DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
        };
        var rotated = Session("forced-access", "forced-refresh");
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => Task.FromResult(rotated),
        };
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-07-11T10:00:00Z")),
            loadSession: async ct =>
            {
                loadEntered.TrySetResult();
                await releaseLoad.Task.WaitAsync(ct);
                return current;
            });

        var nonForced = service.RefreshAsync(false, default);
        await loadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var forced = service.RefreshAsync(true, default);
        releaseLoad.SetResult();

        AssertEqual(current, await nonForced,
            "non-forced operation should retain a session that is not due");
        AssertEqual(rotated, await forced,
            "forced caller should receive a genuinely refreshed session");
        AssertEqual(1, client.RefreshCalls,
            "forced caller should invoke client after concurrent no-op completes");
    }

    private static async Task ForcedRefreshJoinsConcurrentRealRefreshAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var refreshEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Session("due-access", "due-refresh") with
        {
            AccessExpiresAt = DateTimeOffset.Parse("2026-07-11T09:00:00Z"),
        };
        var rotated = Session("joined-access", "joined-refresh");
        var client = new FakeAuthClient
        {
            Refresh = async (_, ct) =>
            {
                refreshEntered.TrySetResult();
                await releaseRefresh.Task.WaitAsync(ct);
                return rotated;
            },
        };
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            timeProvider: new FixedTimeProvider(DateTimeOffset.Parse("2026-07-11T10:00:00Z")));
        await service.SaveLoginAsync(current, default);

        var nonForced = service.RefreshAsync(false, default);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var forced = service.RefreshAsync(true, default);
        releaseRefresh.SetResult();

        var results = await Task.WhenAll(nonForced, forced);
        AssertEqual(rotated, results[0],
            "non-forced caller should receive rotated session");
        AssertEqual(rotated, results[1],
            "forced caller should share the rotated session");
        AssertEqual(1, client.RefreshCalls,
            "forced caller should not repeat an in-flight network refresh");
    }

    private static async Task RefreshPersistsBeforePublicationAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var oldSession = Session("old-visible-access", "old-visible-refresh");
        var newSession = Session("new-hidden-access", "new-hidden-refresh");
        var saveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => Task.FromResult(newSession),
        };
        await using var service = Service(
            client,
            store,
            saveSession: async (session, ct) =>
            {
                if (session.AccessToken == newSession.AccessToken)
                {
                    saveEntered.TrySetResult();
                    await releaseSave.Task.WaitAsync(ct);
                }

                await store.SaveAsync(session, ct);
            });
        await service.SaveLoginAsync(oldSession, default);

        var refresh = service.RefreshAsync(true, default);
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        AssertEqual(oldSession, await service.GetSessionAsync(default),
            "unpersisted refresh must not be visible through service snapshot");
        releaseSave.SetResult();
        AssertEqual(newSession, await refresh,
            "refresh caller should receive session after persistence completes");
        AssertEqual(newSession, await service.GetSessionAsync(default),
            "persisted refresh should become visible through service snapshot");
    }

    private static async Task StatusAndErrorsRedactCredentialsAsync()
    {
        const string accessToken = "status-access-token-secret";
        const string refreshToken = "status-refresh-token-secret";
        using var directory = new AuthTemporaryDirectory();
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => throw new CatpawAuthException(
                $"rejected {accessToken} using {refreshToken}",
                CatpawAuthFailureKind.AuthRejected),
        };
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")));
        await service.SaveLoginAsync(Session(accessToken, refreshToken), default);

        var error = await AssertThrowsAsync<CatpawAuthException>(
            () => service.RefreshAsync(true, default),
            "rejected refresh should expose a redacted service error");
        var status = service.GetStatus();
        var visibleText = string.Join(
            Environment.NewLine,
            JsonSerializer.Serialize(status),
            status.ToString(),
            error.ToString());

        AssertTrue(!visibleText.Contains(accessToken, StringComparison.Ordinal),
            "service-visible status and errors must redact access token");
        AssertTrue(!visibleText.Contains(refreshToken, StringComparison.Ordinal),
            "service-visible status and errors must redact refresh token");
    }

    private static async Task NewerLoginDiscardsStaleRefreshAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var refreshEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRefresh = Session("stale-refresh-access", "stale-refresh-token");
        var newerLogin = Session("new-login-access", "new-login-refresh");
        var client = new FakeAuthClient
        {
            Refresh = async (_, ct) =>
            {
                refreshEntered.TrySetResult();
                await releaseRefresh.Task.WaitAsync(ct);
                return staleRefresh;
            },
        };
        await using var service = Service(client, store);
        await service.SaveLoginAsync(Session("base-access", "base-refresh"), default);

        var refresh = service.RefreshAsync(true, default);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.SaveLoginAsync(newerLogin, default);
        releaseRefresh.SetResult();

        AssertEqual(newerLogin, await refresh,
            "stale refresh caller should receive the newer login session");
        AssertEqual(newerLogin, await service.GetSessionAsync(default),
            "stale refresh must not replace newer login in memory");
        AssertEqual(newerLogin, await store.LoadAsync(),
            "stale refresh must not replace newer login in store");
    }

    private static async Task RefreshAndLoginPersistenceIsSerializedAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var refreshSaveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefreshSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loginSaveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = Session("serialized-refresh-access", "serialized-refresh-token");
        var newerLogin = Session("serialized-login-access", "serialized-login-refresh");
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => Task.FromResult(refreshed),
        };
        await using var service = Service(
            client,
            store,
            saveSession: async (session, ct) =>
            {
                if (session.AccessToken == refreshed.AccessToken)
                {
                    refreshSaveEntered.TrySetResult();
                    await releaseRefreshSave.Task.WaitAsync(ct);
                }
                else if (session.AccessToken == newerLogin.AccessToken)
                {
                    loginSaveEntered.TrySetResult();
                }

                await store.SaveAsync(session, ct);
            });
        await service.SaveLoginAsync(Session("serialized-base", "serialized-base-refresh"),
            default);

        var refresh = service.RefreshAsync(true, default);
        await refreshSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var login = service.SaveLoginAsync(newerLogin, default);

        AssertTrue(!loginSaveEntered.Task.IsCompleted,
            "login persistence must wait for active refresh mutation section");
        releaseRefreshSave.SetResult();
        await Task.WhenAll(refresh, login);
        AssertEqual(newerLogin, await service.GetSessionAsync(default),
            "newer serialized login should win in memory");
        AssertEqual(newerLogin, await store.LoadAsync(),
            "newer serialized login should win in store");
    }

    private static async Task NewerLoginDiscardsStaleAuthRejectionAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var refreshEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newerLogin = Session("rejection-login-access", "rejection-login-refresh");
        var client = new FakeAuthClient
        {
            Refresh = async (_, ct) =>
            {
                refreshEntered.TrySetResult();
                await releaseRefresh.Task.WaitAsync(ct);
                throw new CatpawAuthException(
                    "redacted stale rejection", CatpawAuthFailureKind.AuthRejected);
            },
        };
        await using var service = Service(client, store);
        await service.SaveLoginAsync(Session("rejection-base", "rejection-base-refresh"),
            default);

        var refresh = service.RefreshAsync(true, default);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.SaveLoginAsync(newerLogin, default);
        releaseRefresh.SetResult();

        AssertEqual(newerLogin, await refresh,
            "stale rejection caller should receive newer login session");
        AssertTrue(service.GetStatus().SignedIn,
            "stale rejection must not mark newer login as signed out");
        AssertEqual(newerLogin, await store.LoadAsync(),
            "stale rejection must not alter newer stored login");
    }

    private static async Task NewerLoginPreventsStaleRetryAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var retryDelayEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetryDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newerLogin = Session("retry-login-access", "retry-login-refresh");
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => throw new CatpawAuthException(
                "redacted stale transient", CatpawAuthFailureKind.Transient),
        };
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: async (duration, ct) =>
            {
                if (duration == TimeSpan.FromSeconds(1))
                {
                    retryDelayEntered.TrySetResult();
                    await releaseRetryDelay.Task.WaitAsync(ct);
                }
            });
        await service.SaveLoginAsync(Session("retry-base", "retry-base-refresh"), default);

        var refresh = service.RefreshAsync(true, default);
        await retryDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.SaveLoginAsync(newerLogin, default);
        releaseRetryDelay.SetResult();

        AssertEqual(newerLogin, await refresh,
            "retry delayed on stale credentials should return newer login");
        AssertEqual(1, client.RefreshCalls,
            "newer login during retry delay should prevent another stale request");
    }

    private static async Task LoginWinsAuthFailureStatusRaceAsync()
    {
        await LoginWinsFailureStatusRaceAsync(
            CatpawAuthFailureKind.AuthRejected,
            "auth rejection");
    }

    private static async Task LoginWinsTransientFailureStatusRaceAsync()
    {
        await LoginWinsFailureStatusRaceAsync(
            CatpawAuthFailureKind.Transient,
            "transient failure");
    }

    private static async Task LoginWinsFailureStatusRaceAsync(
        CatpawAuthFailureKind failureKind,
        string scenario)
    {
        using var directory = new AuthTemporaryDirectory();
        var failureChecked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStatusMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newerLogin = Session(
            $"{failureKind}-login-access",
            $"{failureKind}-login-refresh");
        var client = new FakeAuthClient
        {
            Refresh = (_, _) => throw new CatpawAuthException(
                $"redacted {scenario}", failureKind),
        };
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        await using var service = Service(
            client,
            store,
            delay: (_, _) => Task.CompletedTask,
            beforeFailureStatusMutation: async ct =>
            {
                failureChecked.TrySetResult();
                await releaseStatusMutation.Task.WaitAsync(ct);
            });
        await service.SaveLoginAsync(Session("race-base", "race-base-refresh"), default);

        var refresh = service.RefreshAsync(true, default);
        await failureChecked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.SaveLoginAsync(newerLogin, default);
        releaseStatusMutation.SetResult();

        AssertEqual(newerLogin, await refresh,
            $"newer login should supersede stale {scenario}");
        var status = service.GetStatus();
        AssertTrue(status.SignedIn,
            $"stale {scenario} must not mark newer login signed out or pending");
        AssertEqual("SignedIn", status.State,
            $"stale {scenario} must preserve newer login status");
        AssertEqual(newerLogin, await store.LoadAsync(),
            $"stale {scenario} must not change newer stored login");
    }

    private static async Task SchedulerWakesWhenLoginIsSavedAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var now = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var scheduled = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(
            new FakeAuthClient(),
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: (duration, ct) =>
            {
                scheduled.TrySetResult(duration);
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            timeProvider: new FixedTimeProvider(now));

        await service.StartAsync(default);
        await service.SaveLoginAsync(Session("signed-in-access", "signed-in-refresh") with
        {
            AccessExpiresAt = now.AddMinutes(20),
        }, default);

        AssertEqual(TimeSpan.FromMinutes(15),
            await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            "signed-out scheduler should wake immediately for saved login");
        await service.StopAsync(default);
    }

    private static async Task SchedulerWakesForEarlierExpiryAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var now = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var firstDelay = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recalculatedDelay = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCalls = 0;
        await using var service = Service(
            new FakeAuthClient(),
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: (duration, ct) =>
            {
                if (Interlocked.Increment(ref delayCalls) == 1)
                {
                    firstDelay.TrySetResult(duration);
                }
                else
                {
                    recalculatedDelay.TrySetResult(duration);
                }

                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            timeProvider: new FixedTimeProvider(now));
        await service.SaveLoginAsync(Session("later-access", "later-refresh") with
        {
            AccessExpiresAt = now.AddHours(1),
        }, default);

        await service.StartAsync(default);
        AssertEqual(TimeSpan.FromMinutes(55),
            await firstDelay.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            "initial deadline should be five minutes before later expiry");
        await service.SaveLoginAsync(Session("earlier-access", "earlier-refresh") with
        {
            AccessExpiresAt = now.AddMinutes(10),
        }, default);

        AssertEqual(TimeSpan.FromMinutes(5),
            await recalculatedDelay.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            "earlier replacement should cancel and recalculate pending deadline");
        await service.StopAsync(default);
    }

    private static async Task RefreshRejectsPreCancelledCallerAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var client = new FakeAuthClient();
        await using var service = Service(
            client,
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")));
        await service.SaveLoginAsync(Session("cancelled-access", "cancelled-refresh"), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(
            () => service.RefreshAsync(true, cancellation.Token),
            "pre-cancelled refresh should reject caller");

        AssertEqual(0, client.RefreshCalls,
            "pre-cancelled refresh must not start client work");
    }

    private static async Task CancelledStopRetainsSchedulerOwnershipAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var delayCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCalls = 0;
        await using var service = Service(
            new FakeAuthClient(),
            new AuthSessionStore(Path.Combine(directory.Path, "session.json")),
            delay: async (_, ct) =>
            {
                Interlocked.Increment(ref delayCalls);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    delayCancelled.TrySetResult();
                    await releaseCleanup.Task;
                    throw;
                }
            });
        await service.SaveLoginAsync(Session("stop-access", "stop-refresh"), default);
        await service.StartAsync(default);
        using var stopCancellation = new CancellationTokenSource();

        var stop = service.StopAsync(stopCancellation.Token);
        await delayCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        stopCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            () => stop,
            "cancelled stop caller should stop waiting");
        await service.StartAsync(default);
        var callsBeforeOldSchedulerExit = Volatile.Read(ref delayCalls);

        releaseCleanup.SetResult();
        await service.StopAsync(default);
        AssertEqual(1, callsBeforeOldSchedulerExit,
            "start must not replace scheduler before cancelled stop target terminates");
        await service.StartAsync(default);
        AssertEqual(2, Volatile.Read(ref delayCalls),
            "start should create one scheduler after prior ownership is cleaned up");
        await service.StopAsync(default);
    }

    private static async Task DisposalAwaitsActiveRefreshAsync()
    {
        using var directory = new AuthTemporaryDirectory();
        var refreshEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = Session("disposed-access", "disposed-refresh");
        var refreshedSaveCalls = 0;
        var client = new FakeAuthClient
        {
            Refresh = async (_, ct) =>
            {
                refreshEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    refreshCancelled.TrySetResult();
                    await releaseCleanup.Task;
                    return refreshed;
                }

                throw new InvalidOperationException("unreachable");
            },
        };
        var store = new AuthSessionStore(Path.Combine(directory.Path, "session.json"));
        var service = Service(
            client,
            store,
            saveSession: async (session, ct) =>
            {
                if (session.AccessToken == refreshed.AccessToken)
                {
                    Interlocked.Increment(ref refreshedSaveCalls);
                }

                await store.SaveAsync(session, ct);
            });
        await service.SaveLoginAsync(Session("dispose-base", "dispose-base-refresh"), default);
        var refresh = service.RefreshAsync(true, default);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var dispose = service.DisposeAsync().AsTask();
        await refreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disposeReturnedBeforeRefreshCleanup = dispose.IsCompleted;
        releaseCleanup.SetResult();
        await dispose;
        await AssertThrowsAsync<OperationCanceledException>(
            () => refresh,
            "disposed refresh must not publish after client cleanup");

        AssertTrue(!disposeReturnedBeforeRefreshCleanup,
            "dispose should await active refresh cleanup");
        AssertEqual(0, refreshedSaveCalls,
            "disposed refresh must not persist client result");
    }

    private static CatpawAuthService Service(
        ICatpawAuthClient client,
        AuthSessionStore store,
        Func<string, CancellationToken, Task<string>>? desktopStateReader = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<AuthSession?>>? loadSession = null,
        Func<AuthSession, CancellationToken, Task>? saveSession = null,
        Func<CancellationToken, Task>? beforeFailureStatusMutation = null) => new(
            client, store, Tenant,
            desktopStateReader is null ? null : new DesktopStateReader(desktopStateReader),
            delay, timeProvider,
            loadSession, saveSession, beforeFailureStatusMutation);

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
