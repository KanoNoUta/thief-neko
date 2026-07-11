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
        yield return ("login state requires valid uppercase invitation code", InvitationAsync);
        yield return ("login state retries SMS after Yoda success", YodaSuccessAsync);
        yield return ("login state resets SMS after Yoda failure", YodaFailureAsync);
        yield return ("login state disposal cancels active work", DisposalCancelsAsync);
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
        AssertEqual(LoginPhase.WaitingForSmsCode, state.Phase,
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

    private static Task InvitationAsync()
    {
        using var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);
        state.BeginSmsRequest("13800138000");
        state.SmsSent(new SmsChallenge("sms-uuid", null));

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

    private static LoginStateMachine ReadyForSms()
    {
        var state = new LoginStateMachine();
        state.SwitchMode(LoginMode.Mobile);
        state.SetTermsAccepted(true);
        state.BeginSmsRequest("13800138000");
        return state;
    }

    private static QrLoginChallenge Challenge() => new(
        "qr-code-secret",
        "https://example.invalid/qr.png",
        DateTimeOffset.UtcNow.AddMinutes(2));

    private static AuthSession Session() => new(
        "access-secret",
        "refresh-secret",
        "user-1",
        "Test Account",
        "tenant",
        null,
        DateTimeOffset.UtcNow.AddHours(1),
        DateTimeOffset.UtcNow);
}
