using CatapiController;
using static CatapiController.Tests.AuthTestSupport;

namespace CatapiController.Tests;

internal static class LoginStateTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("login state advances through QR sign-in", QrTransitionsAsync);
        yield return ("login state handles QR expiry retry and cancel", QrExpiryRetryCancelAsync);
        yield return ("login state preserves QR context for mobile binding", MobileBindingAsync);
        yield return ("login state validates mobile input and SMS countdown", SmsCountdownAsync);
        yield return ("login state rejects verification before SMS is sent",
            VerificationRequiresSmsAsync);
        yield return ("login state rejects invalid verification and login transitions",
            InvalidMobileTransitionsAsync);
        yield return ("login state requires valid uppercase invitation code", InvitationAsync);
        yield return ("login state retries SMS after Yoda success", YodaSuccessAsync);
        yield return ("login state resets SMS after Yoda failure", YodaFailureAsync);
        yield return ("login state disposal cancels active work", DisposalCancelsAsync);
        yield return ("login flow verifies logs in saves and clears once",
            CompleteMobileLoginAsync);
        yield return ("login flow invitation continues without second verification",
            InvitationContinuationAsync);
        yield return ("login flow binds QR account instead of mobile login",
            CompleteMobileBindingAsync);
        yield return ("login flow rejects submit before successful SMS",
            FlowRejectsPrematureSubmitAsync);
        yield return ("login flow disposal clears pending verified input",
            FlowDisposalClearsInputAsync);
        yield return ("login flow ignores QR poll result crossing expiry",
            DeferredQrPollExpiresAsync);
        yield return ("Yoda close cancels and awaits initialization before cleanup",
            YodaCloseDuringInitializationAsync);
    }

    private static Task QrTransitionsAsync()
    {
        using var state = new LoginStateMachine();

        state.BeginQrRequest();
        AssertEqual(LoginPhase.RequestingQr, state.Phase, "QR request should start");
        state.QrReceived(Challenge());
        AssertEqual(LoginPhase.WaitingForAgreement, state.Phase,
            "QR polling must wait for explicit terms agreement");
        state.SetTermsAccepted(true);
        AssertEqual(LoginPhase.WaitingForScan, state.Phase,
            "accepting terms should enable QR polling");
        state.ApplyQrPoll(new QrLoginPoll("pending", null, false));
        AssertEqual(LoginPhase.WaitingForScan, state.Phase, "pending QR should keep waiting");
        state.ApplyQrPoll(new QrLoginPoll("scanned", null, false));
        AssertEqual(LoginPhase.Scanned, state.Phase, "scanned QR should await confirmation");
        state.ApplyQrPoll(new QrLoginPoll("confirmed", Session(), false));
        AssertEqual(LoginPhase.SignedIn, state.Phase, "confirmed QR should sign in");

        return Task.CompletedTask;
    }

    private static Task QrExpiryRetryCancelAsync()
    {
        using var state = new LoginStateMachine();
        var first = state.BeginQrRequest();
        state.QrReceived(Challenge());
        state.SetTermsAccepted(true);

        state.MarkQrExpired();
        AssertEqual(LoginPhase.Expired, state.Phase, "expired QR should stop polling");
        AssertTrue(first.IsCancellationRequested, "expiry should cancel active QR work");

        var retry = state.RetryQr();
        AssertEqual(LoginPhase.RequestingQr, state.Phase, "retry should request a fresh QR");
        AssertTrue(state.QrImageUrl is null, "retry should discard expired QR metadata");
        state.Cancel();
        AssertEqual(LoginPhase.Cancelled, state.Phase, "cancel should be terminal");
        AssertTrue(retry.IsCancellationRequested, "cancel should stop retry work");

        return Task.CompletedTask;
    }

    private static Task MobileBindingAsync()
    {
        using var state = new LoginStateMachine();
        state.BeginQrRequest();
        state.QrReceived(Challenge());
        state.SetTermsAccepted(true);

        state.ApplyQrPoll(new QrLoginPoll("scanned", null, true));

        AssertEqual(LoginMode.Mobile, state.Mode, "binding should switch to mobile mode");
        AssertEqual(LoginPhase.NeedsMobileBinding, state.Phase,
            "unbound scanned account should enter binding");
        AssertEqual("qr-code-secret", state.BindingQrCode,
            "binding should retain the in-memory QR context");
        AssertEqual("https://example.invalid/qr.png", state.QrImageUrl,
            "binding should preserve the displayed QR image");
        return Task.CompletedTask;
    }

    private static Task SmsCountdownAsync()
    {
        using var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);

        AssertTrue(LoginStateMachine.IsValidChineseMobile("13800138000"),
            "Chinese 11-digit mobile should be valid");
        AssertTrue(!LoginStateMachine.IsValidChineseMobile("23800138000"),
            "mobile must start with 1 followed by a valid Chinese prefix");
        AssertTrue(!LoginStateMachine.IsValidChineseMobile("1380013800x"),
            "mobile must contain digits only");
        AssertTrue(LoginStateMachine.IsValidSmsCode("123456"),
            "six-digit SMS code should be valid");
        AssertTrue(!LoginStateMachine.IsValidSmsCode("12345"),
            "short SMS code should be invalid");

        state.BeginSmsRequest("13800138000");
        state.SmsSent(new SmsChallenge("sms-uuid", null));
        AssertEqual(LoginPhase.CodeEntry, state.Phase,
            "SMS request should wait for a code");
        AssertEqual(60, state.CountdownSeconds, "SMS resend countdown should start at 60");
        AssertTrue(!state.CanSendSms, "SMS resend should be disabled during countdown");
        state.TickCountdown(59);
        AssertEqual(1, state.CountdownSeconds, "countdown should advance deterministically");
        state.TickCountdown();
        AssertEqual(0, state.CountdownSeconds, "countdown should stop at zero");
        AssertTrue(state.CanSendSms, "SMS resend should be enabled at zero");
        return Task.CompletedTask;
    }

    private static Task VerificationRequiresSmsAsync()
    {
        using var mobile = new LoginStateMachine();
        mobile.SwitchMode(LoginMode.Mobile);
        mobile.SetTermsAccepted(true);

        AssertThrows<InvalidOperationException>(
            () => mobile.BeginVerification("13800138000", "123456"),
            "mobile entry must not verify before successful SendSms");

        using var binding = new LoginStateMachine();
        binding.BeginQrRequest();
        binding.QrReceived(Challenge());
        binding.SetTermsAccepted(true);
        binding.ApplyQrPoll(new QrLoginPoll("scanned", null, true));
        binding.SetTermsAccepted(true);

        AssertThrows<InvalidOperationException>(
            () => binding.BeginVerification("13800138000", "123456"),
            "mobile binding must not verify before successful SendSms");
        return Task.CompletedTask;
    }

    private static Task InvalidMobileTransitionsAsync()
    {
        using var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);
        state.BeginSmsRequest("13800138000");

        AssertThrows<InvalidOperationException>(
            () => state.BeginVerification("13800138000", "123456"),
            "verification must wait for SendSms completion");
        AssertThrows<InvalidOperationException>(
            () => state.ApplyVerification(new MobileVerification(true, false)),
            "verification result must require an active verification");

        state.SmsSent(new SmsChallenge("sms-uuid", null));
        AssertEqual(LoginPhase.CodeEntry, state.Phase,
            "successful SendSms should explicitly enter code entry");
        AssertThrows<InvalidOperationException>(
            () => state.BeginLogin(null),
            "login must require successful verification");
        return Task.CompletedTask;
    }

    private static Task InvitationAsync()
    {
        using var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);
        state.BeginSmsRequest("13800138000");
        state.SmsSent(new SmsChallenge("sms-uuid", null));

        state.BeginVerification("13800138000", "123456");
        state.ApplyVerification(new MobileVerification(true, true));

        AssertEqual(LoginPhase.NeedsInvitation, state.Phase,
            "verification should expose required invitation input");
        AssertTrue(LoginStateMachine.IsValidInvitation("ABC123"),
            "six uppercase alphanumeric invitation should be valid");
        AssertTrue(!LoginStateMachine.IsValidInvitation("abc123"),
            "lowercase invitation should be rejected");
        AssertTrue(!LoginStateMachine.IsValidInvitation("ABCDE"),
            "invitation must contain exactly six characters");
        return Task.CompletedTask;
    }

    private static Task YodaSuccessAsync()
    {
        using var state = ReadyForSms();
        state.SmsSent(new SmsChallenge("sms-uuid", "request-code"));
        AssertEqual(LoginPhase.YodaChallenge, state.Phase,
            "requestCode should require Yoda");

        state.ApplyYodaResult(true);

        AssertEqual(LoginPhase.SendingSms, state.Phase,
            "successful Yoda should retry the SMS request");
        AssertTrue(state.ActiveToken.CanBeCanceled,
            "Yoda retry should own a cancellable operation");
        return Task.CompletedTask;
    }

    private static Task YodaFailureAsync()
    {
        using var state = ReadyForSms();
        state.SmsSent(new SmsChallenge("sms-uuid", "request-code"));

        state.ApplyYodaResult(false);

        AssertEqual(LoginPhase.MobileEntry, state.Phase,
            "failed Yoda should reset to mobile entry");
        AssertTrue(state.PendingRequestCode is null,
            "failed Yoda should clear the pending request code");
        return Task.CompletedTask;
    }

    private static Task DisposalCancelsAsync()
    {
        var state = new LoginStateMachine();
        var token = state.BeginQrRequest();

        state.Dispose();

        AssertTrue(token.IsCancellationRequested, "dispose should cancel active work");
        AssertEqual(LoginPhase.Disposed, state.Phase, "dispose should be terminal");
        AssertTrue(state.BindingQrCode is null, "dispose should clear in-memory QR context");
        return Task.CompletedTask;
    }

    private static async Task CompleteMobileLoginAsync()
    {
        using var state = CodeEntryState();
        var client = new FakeLoginClient
        {
            Verification = new MobileVerification(true, false),
        };
        var saved = new List<AuthSession>();
        var clearCalls = 0;
        using var flow = new LoginFlowController(
            state,
            client,
            (session, _) =>
            {
                saved.Add(session);
                return Task.CompletedTask;
            },
            () => clearCalls++);

        var outcome = await flow.SubmitMobileAsync(
            "13800138000", "123456", default);

        AssertEqual(MobileSubmitOutcome.SignedIn, outcome,
            "verified mobile should complete login");
        AssertEqual(1, client.VerifyCalls, "mobile should verify exactly once");
        AssertEqual(1, client.LoginCalls, "mobile login endpoint should be called once");
        AssertEqual(0, client.BindCalls, "plain mobile login must not bind QR context");
        AssertEqual(1, saved.Count, "successful login should invoke SaveLogin once");
        AssertEqual(client.Session, saved[0], "SaveLogin should receive returned session");
        AssertEqual(1, clearCalls, "successful login should clear inputs once");
        AssertTrue(!flow.HasVerifiedMobileContext,
            "successful login should clear verified mobile context");
        AssertEqual(LoginPhase.SignedIn, state.Phase, "successful login should be terminal");
    }

    private static async Task InvitationContinuationAsync()
    {
        using var state = CodeEntryState();
        var client = new FakeLoginClient
        {
            Verification = new MobileVerification(true, true),
        };
        var saveCalls = 0;
        var clearCalls = 0;
        using var flow = new LoginFlowController(
            state,
            client,
            (_, _) =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            () => clearCalls++);

        var first = await flow.SubmitMobileAsync("13800138000", "123456", default);

        AssertEqual(MobileSubmitOutcome.InvitationRequired, first,
            "verified account should pause for invitation");
        AssertEqual(1, client.VerifyCalls, "initial submit should verify once");
        AssertEqual(0, client.LoginCalls, "invitation branch should not log in yet");
        AssertEqual(0, saveCalls, "invitation branch should not persist yet");
        AssertEqual(0, clearCalls, "verified context must survive until invitation");
        AssertTrue(flow.HasVerifiedMobileContext,
            "verified mobile and SMS should remain in memory for continuation");

        var second = await flow.ContinueInvitationAsync("ABC123", default);

        AssertEqual(MobileSubmitOutcome.SignedIn, second,
            "valid invitation should continue directly to login");
        AssertEqual(1, client.VerifyCalls,
            "invitation continuation must not call VerifySms again");
        AssertEqual(1, client.LoginCalls, "invitation should invoke login once");
        AssertEqual("13800138000", client.LastMobile,
            "continuation should use verified in-memory mobile");
        AssertEqual("123456", client.LastSmsCode,
            "continuation should use verified in-memory SMS code");
        AssertEqual("ABC123", client.LastInvitation,
            "continuation should submit the invitation");
        AssertEqual(1, saveCalls, "invitation login should persist once");
        AssertEqual(1, clearCalls, "invitation login should clear inputs once");
        AssertTrue(!flow.HasVerifiedMobileContext,
            "invitation continuation should clear verified context");
    }

    private static async Task CompleteMobileBindingAsync()
    {
        using var state = BindingCodeEntryState();
        var client = new FakeLoginClient
        {
            Verification = new MobileVerification(true, false),
        };
        var saveCalls = 0;
        using var flow = new LoginFlowController(
            state,
            client,
            (_, _) =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            () => { });

        var outcome = await flow.SubmitMobileAsync(
            "13800138000", "123456", default);

        AssertEqual(MobileSubmitOutcome.SignedIn, outcome,
            "verified binding should complete sign-in");
        AssertEqual(0, client.LoginCalls, "binding must not call mobile login");
        AssertEqual(1, client.BindCalls, "binding endpoint should be called once");
        AssertEqual("qr-code-secret", client.LastQrCode,
            "binding should submit preserved QR context");
        AssertEqual(1, saveCalls, "binding should persist returned session once");
    }

    private static async Task FlowRejectsPrematureSubmitAsync()
    {
        using var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);
        var client = new FakeLoginClient();
        using var flow = new LoginFlowController(
            state, client, (_, _) => Task.CompletedTask, () => { });

        await AssertThrowsAsync<InvalidOperationException>(
            () => flow.SubmitMobileAsync("13800138000", "123456", default),
            "flow must reject submit before SendSms succeeds");

        AssertEqual(0, client.VerifyCalls, "invalid transition must not call VerifySms");
        AssertEqual(0, client.LoginCalls, "invalid transition must not call login");
    }

    private static async Task FlowDisposalClearsInputAsync()
    {
        using var state = CodeEntryState();
        var client = new FakeLoginClient
        {
            Verification = new MobileVerification(true, true),
        };
        var clearCalls = 0;
        var flow = new LoginFlowController(
            state, client, (_, _) => Task.CompletedTask, () => clearCalls++);
        await flow.SubmitMobileAsync("13800138000", "123456", default);

        flow.Dispose();

        AssertTrue(!flow.HasVerifiedMobileContext,
            "dispose should clear pending verified context");
        AssertEqual(1, clearCalls, "dispose should clear visible inputs once");
    }

    private static async Task DeferredQrPollExpiresAsync()
    {
        var now = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var time = new MutableTimeProvider(now);
        using var state = new LoginStateMachine();
        state.BeginQrRequest();
        state.QrReceived(Challenge(now.AddSeconds(1)));
        state.SetTermsAccepted(true);
        var poll = new TaskCompletionSource<QrLoginPoll>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeLoginClient { DeferredPoll = poll.Task };
        var saveCalls = 0;
        using var flow = new LoginFlowController(
            state,
            client,
            (_, _) =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            () => { },
            time);

        var polling = flow.PollQrOnceAsync(default);
        await client.PollEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));
        poll.SetResult(new QrLoginPoll("confirmed", Session(), false));

        var outcome = await polling;

        AssertEqual(QrPollOutcome.Expired, outcome,
            "poll result arriving after expiry should be ignored");
        AssertEqual(LoginPhase.Expired, state.Phase,
            "late QR result should transition to expired");
        AssertEqual(0, saveCalls, "late confirmed session must not be persisted");
    }

    private static async Task YodaCloseDuringInitializationAsync()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var disposed = 0;
        var cleaned = 0;
        var order = new List<string>();
        var lifecycle = new YodaChallengeLifecycle(
            async ct =>
            {
                entered.TrySetResult();
                await release.Task;
                ct.ThrowIfCancellationRequested();
                created++;
            },
            () =>
            {
                disposed++;
                order.Add("dispose");
            },
            () =>
            {
                cleaned++;
                order.Add("cleanup");
                return Task.CompletedTask;
            });

        var initialization = lifecycle.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var closing = lifecycle.CloseAsync();

        AssertTrue(lifecycle.LifetimeToken.IsCancellationRequested,
            "close should cancel initialization immediately");
        AssertTrue(!closing.IsCompleted,
            "close should await an initializer that is still unwinding");
        AssertEqual(0, cleaned, "profile cleanup must wait for initialization");

        release.SetResult();
        await Task.WhenAll(initialization, closing);
        await lifecycle.CloseAsync();

        AssertEqual(0, created,
            "cancelled initializer must not create WebView after close");
        AssertEqual(1, disposed, "WebView disposal should run exactly once");
        AssertEqual(1, cleaned, "profile cleanup should run exactly once");
        AssertTrue(order.SequenceEqual(new[] { "dispose", "cleanup" }),
            "cleanup should run only after WebView disposal");
    }

    private static LoginStateMachine ReadyForSms()
    {
        var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);
        state.BeginSmsRequest("13800138000");
        return state;
    }

    private static LoginStateMachine CodeEntryState()
    {
        var state = ReadyForSms();
        state.SmsSent(new SmsChallenge("sms-uuid", null));
        return state;
    }

    private static LoginStateMachine BindingCodeEntryState()
    {
        var state = new LoginStateMachine();
        state.BeginQrRequest();
        state.QrReceived(Challenge());
        state.SetTermsAccepted(true);
        state.ApplyQrPoll(new QrLoginPoll("scanned", null, true));
        state.SetTermsAccepted(true);
        state.SetMobile("13800138000");
        state.BeginSmsRequest("13800138000");
        state.SmsSent(new SmsChallenge("sms-uuid", null));
        return state;
    }

    private static QrLoginChallenge Challenge() => Challenge(DateTimeOffset.UtcNow.AddMinutes(2));

    private static QrLoginChallenge Challenge(DateTimeOffset expiresAt) => new(
        "qr-code-secret",
        "https://example.invalid/qr.png",
        expiresAt);

    private static AuthSession Session() => new(
        "access-secret",
        "refresh-secret",
        "user-1",
        "Test Account",
        "tenant",
        null,
        DateTimeOffset.UtcNow.AddHours(1),
        DateTimeOffset.UtcNow);

    private static T AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }

        throw new InvalidOperationException(message);
    }

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

    private sealed class FakeLoginClient : ICatpawLoginClient
    {
        public MobileVerification Verification { get; init; } = new(true, false);
        public AuthSession Session { get; } = LoginStateTests.Session();
        public Task<QrLoginPoll>? DeferredPoll { get; init; }
        public TaskCompletionSource PollEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int VerifyCalls { get; private set; }
        public int LoginCalls { get; private set; }
        public int BindCalls { get; private set; }
        public string? LastMobile { get; private set; }
        public string? LastSmsCode { get; private set; }
        public string? LastInvitation { get; private set; }
        public string? LastQrCode { get; private set; }

        public Task<QrLoginChallenge> CreateQrAsync(CancellationToken ct) =>
            Task.FromResult(Challenge());

        public Task<QrLoginPoll> PollQrAsync(string code, CancellationToken ct)
        {
            PollEntered.TrySetResult();
            return DeferredPoll ?? Task.FromResult(new QrLoginPoll("pending", null, false));
        }

        public Task<SmsChallenge> SendSmsAsync(
            string mobile,
            string deviceId,
            CancellationToken ct) => Task.FromResult(new SmsChallenge("sms-uuid", null));

        public Task<MobileVerification> VerifySmsAsync(
            string mobile,
            string code,
            CancellationToken ct)
        {
            VerifyCalls++;
            return Task.FromResult(Verification);
        }

        public Task<AuthSession> LoginMobileAsync(
            string mobile,
            string code,
            string? invitation,
            CancellationToken ct)
        {
            LoginCalls++;
            Capture(mobile, code, invitation);
            return Task.FromResult(Session);
        }

        public Task<AuthSession> BindMobileAsync(
            string qrCode,
            string mobile,
            string code,
            string? invitation,
            CancellationToken ct)
        {
            BindCalls++;
            LastQrCode = qrCode;
            Capture(mobile, code, invitation);
            return Task.FromResult(Session);
        }

        private void Capture(string mobile, string code, string? invitation)
        {
            LastMobile = mobile;
            LastSmsCode = code;
            LastInvitation = invitation;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
