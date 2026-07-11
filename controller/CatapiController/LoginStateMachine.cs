using System.Text.RegularExpressions;

namespace CatapiController;

internal enum LoginMode
{
    Qr,
    Mobile,
}

internal enum LoginPhase
{
    Idle,
    RequestingQr,
    WaitingForAgreement,
    WaitingForScan,
    Scanned,
    NeedsMobileBinding,
    MobileEntry,
    SendingSms,
    YodaChallenge,
    WaitingForSmsCode,
    VerifyingSms,
    NeedsInvitation,
    SigningIn,
    SignedIn,
    Expired,
    Failed,
    Cancelled,
    Disposed,
}

internal sealed partial class LoginStateMachine : IDisposable
{
    private CancellationTokenSource? _activeOperation;
    private QrLoginChallenge? _qrChallenge;
    private bool _termsAccepted;
    private bool _mobileValid;
    private bool _binding;
    private bool _disposed;

    public LoginMode Mode { get; private set; } = LoginMode.Qr;
    public LoginPhase Phase { get; private set; } = LoginPhase.Idle;
    public string? QrImageUrl => _qrChallenge?.QrCodeUrl;
    public string? ActiveQrCode => _qrChallenge?.Code;
    public DateTimeOffset? QrExpiresAt => _qrChallenge?.ExpiresAt;
    public string? BindingQrCode => _binding ? _qrChallenge?.Code : null;
    public string? PendingRequestCode { get; private set; }
    public int CountdownSeconds { get; private set; }
    public bool TermsAccepted => _termsAccepted;
    public bool IsBinding => _binding;
    public bool CanSendSms => !_disposed && _termsAccepted && _mobileValid &&
        CountdownSeconds == 0 && Phase is LoginPhase.MobileEntry or
            LoginPhase.NeedsMobileBinding or LoginPhase.WaitingForSmsCode;
    public CancellationToken ActiveToken => _activeOperation?.Token ?? CancellationToken.None;

    public CancellationToken BeginQrRequest()
    {
        ThrowIfDisposed();
        Mode = LoginMode.Qr;
        _binding = false;
        _termsAccepted = false;
        _mobileValid = false;
        ClearQrContext();
        PendingRequestCode = null;
        CountdownSeconds = 0;
        Phase = LoginPhase.RequestingQr;
        return BeginOperation();
    }

    public void QrReceived(QrLoginChallenge challenge)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(challenge);
        if (Phase != LoginPhase.RequestingQr)
        {
            throw new InvalidOperationException("QR challenge was not requested.");
        }

        _qrChallenge = challenge;
        Phase = _termsAccepted ? LoginPhase.WaitingForScan : LoginPhase.WaitingForAgreement;
    }

    public void SetTermsAccepted(bool accepted)
    {
        ThrowIfDisposed();
        _termsAccepted = accepted;
        if (Mode == LoginMode.Qr && _qrChallenge is not null &&
            Phase is LoginPhase.WaitingForAgreement or LoginPhase.WaitingForScan)
        {
            Phase = accepted ? LoginPhase.WaitingForScan : LoginPhase.WaitingForAgreement;
        }
    }

    public void ApplyQrPoll(QrLoginPoll poll)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(poll);
        if (!_termsAccepted || _qrChallenge is null)
        {
            throw new InvalidOperationException("QR polling requires an accepted active challenge.");
        }

        if (poll.RequiresMobileBinding)
        {
            CancelActiveOperation();
            Mode = LoginMode.Mobile;
            _binding = true;
            _termsAccepted = false;
            _mobileValid = false;
            Phase = LoginPhase.NeedsMobileBinding;
            return;
        }

        Phase = poll.Status switch
        {
            "pending" => LoginPhase.WaitingForScan,
            "scanned" => LoginPhase.Scanned,
            "confirmed" or "mobileBound" when poll.Session is not null => LoginPhase.SignedIn,
            _ => throw new InvalidOperationException("Unsupported QR poll transition."),
        };

    }

    public void MarkQrExpired()
    {
        ThrowIfDisposed();
        CancelActiveOperation();
        Phase = LoginPhase.Expired;
    }

    public CancellationToken RetryQr() => BeginQrRequest();

    public void SwitchMode(LoginMode mode)
    {
        ThrowIfDisposed();
        CancelActiveOperation();
        Mode = mode;
        _termsAccepted = false;
        _mobileValid = false;
        PendingRequestCode = null;
        CountdownSeconds = 0;
        if (mode == LoginMode.Qr)
        {
            _binding = false;
            ClearQrContext();
            Phase = LoginPhase.Idle;
        }
        else
        {
            _binding = false;
            ClearQrContext();
            Phase = LoginPhase.MobileEntry;
        }
    }

    public void SetMobile(string mobile)
    {
        ThrowIfDisposed();
        _mobileValid = IsValidChineseMobile(mobile);
    }

    public CancellationToken BeginSmsRequest(string mobile)
    {
        ThrowIfDisposed();
        _mobileValid = IsValidChineseMobile(mobile);
        if (!_termsAccepted || !_mobileValid || CountdownSeconds > 0)
        {
            throw new InvalidOperationException("Mobile and terms must be valid before requesting SMS.");
        }

        PendingRequestCode = null;
        Phase = LoginPhase.SendingSms;
        return BeginOperation();
    }

    public void SmsSent(SmsChallenge challenge)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(challenge);
        if (Phase != LoginPhase.SendingSms)
        {
            throw new InvalidOperationException("SMS request is not active.");
        }

        PendingRequestCode = challenge.RequestCode;
        if (!string.IsNullOrWhiteSpace(challenge.RequestCode))
        {
            Phase = LoginPhase.YodaChallenge;
            return;
        }

        CancelActiveOperation();
        CountdownSeconds = 60;
        Phase = LoginPhase.WaitingForSmsCode;
    }

    public void TickCountdown(int seconds = 1)
    {
        ThrowIfDisposed();
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        CountdownSeconds = Math.Max(0, CountdownSeconds - seconds);
    }

    public CancellationToken BeginVerification(string mobile, string smsCode)
    {
        ThrowIfDisposed();
        if (!IsValidChineseMobile(mobile) || !IsValidSmsCode(smsCode))
        {
            throw new InvalidOperationException("Mobile verification input is invalid.");
        }

        Phase = LoginPhase.VerifyingSms;
        return BeginOperation();
    }

    public void ApplyVerification(MobileVerification verification)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(verification);
        CancelActiveOperation();
        if (!verification.Verified)
        {
            Phase = LoginPhase.WaitingForSmsCode;
            return;
        }

        Phase = verification.InvitationCodeRequired
            ? LoginPhase.NeedsInvitation
            : LoginPhase.SigningIn;
    }

    public CancellationToken BeginLogin(string? invitation)
    {
        ThrowIfDisposed();
        if (Phase == LoginPhase.NeedsInvitation && !IsValidInvitation(invitation))
        {
            throw new InvalidOperationException("Invitation code is invalid.");
        }

        Phase = LoginPhase.SigningIn;
        return BeginOperation();
    }

    public void CompleteSignIn()
    {
        ThrowIfDisposed();
        CancelActiveOperation();
        ClearSensitiveContext();
        Phase = LoginPhase.SignedIn;
    }

    public void ApplyYodaResult(bool succeeded)
    {
        ThrowIfDisposed();
        if (Phase != LoginPhase.YodaChallenge)
        {
            throw new InvalidOperationException("Yoda challenge is not active.");
        }

        PendingRequestCode = null;
        CancelActiveOperation();
        if (succeeded)
        {
            Phase = LoginPhase.SendingSms;
            BeginOperation();
        }
        else
        {
            Phase = _binding ? LoginPhase.NeedsMobileBinding : LoginPhase.MobileEntry;
        }
    }

    public void Fail()
    {
        ThrowIfDisposed();
        CancelActiveOperation();
        PendingRequestCode = null;
        Phase = LoginPhase.Failed;
    }

    public void ResetMobileFailure()
    {
        ThrowIfDisposed();
        Phase = _binding ? LoginPhase.NeedsMobileBinding : LoginPhase.MobileEntry;
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        CancelActiveOperation();
        ClearSensitiveContext();
        Phase = LoginPhase.Cancelled;
    }

    public static bool IsValidChineseMobile(string? mobile) =>
        mobile is not null && ChineseMobileRegex().IsMatch(mobile);

    public static bool IsValidSmsCode(string? code) =>
        code is not null && SmsCodeRegex().IsMatch(code);

    public static bool IsValidInvitation(string? invitation) =>
        invitation is not null && InvitationRegex().IsMatch(invitation);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelActiveOperation();
        ClearSensitiveContext();
        Phase = LoginPhase.Disposed;
    }

    private CancellationToken BeginOperation()
    {
        CancelActiveOperation();
        _activeOperation = new CancellationTokenSource();
        return _activeOperation.Token;
    }

    private void CancelActiveOperation()
    {
        if (_activeOperation is null)
        {
            return;
        }

        _activeOperation.Cancel();
        _activeOperation.Dispose();
        _activeOperation = null;
    }

    private void ClearSensitiveContext()
    {
        ClearQrContext();
        PendingRequestCode = null;
        CountdownSeconds = 0;
        _mobileValid = false;
        _termsAccepted = false;
        _binding = false;
    }

    private void ClearQrContext() => _qrChallenge = null;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [GeneratedRegex("^1[3-9][0-9]{9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseMobileRegex();

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SmsCodeRegex();

    [GeneratedRegex("^[A-Z0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex InvitationRegex();
}
