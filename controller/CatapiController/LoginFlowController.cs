namespace CatapiController;

internal enum MobileSubmitOutcome
{
    InvalidCode,
    InvitationRequired,
    SignedIn,
}

internal enum QrPollOutcome
{
    Continue,
    BindingRequired,
    SignedIn,
    Expired,
}

internal sealed class LoginFlowController : IDisposable
{
    private sealed record VerifiedMobileContext(string Mobile, string SmsCode);

    private readonly LoginStateMachine _state;
    private readonly ICatpawLoginClient _client;
    private readonly Func<AuthSession, CancellationToken, Task> _saveLogin;
    private readonly Action _clearInputs;
    private readonly TimeProvider _timeProvider;
    private VerifiedMobileContext? _verifiedMobile;
    private bool _inputsCleared;
    private bool _disposed;

    public LoginFlowController(
        LoginStateMachine state,
        ICatpawLoginClient client,
        Func<AuthSession, CancellationToken, Task> saveLogin,
        Action clearInputs,
        TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _saveLogin = saveLogin ?? throw new ArgumentNullException(nameof(saveLogin));
        _clearInputs = clearInputs ?? throw new ArgumentNullException(nameof(clearInputs));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool HasVerifiedMobileContext => _verifiedMobile is not null;

    public async Task<MobileSubmitOutcome> SubmitMobileAsync(
        string mobile,
        string smsCode,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        var operationToken = _state.BeginVerification(mobile, smsCode);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(operationToken, ct);
        var verification = await _client.VerifySmsAsync(mobile, smsCode, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        _state.ApplyVerification(verification);
        if (!verification.Verified)
        {
            return MobileSubmitOutcome.InvalidCode;
        }

        _verifiedMobile = new VerifiedMobileContext(mobile, smsCode);
        if (verification.InvitationCodeRequired)
        {
            return MobileSubmitOutcome.InvitationRequired;
        }

        return await CompleteMobileLoginAsync(null, ct);
    }

    public Task<MobileSubmitOutcome> ContinueInvitationAsync(
        string invitation,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_state.Phase != LoginPhase.NeedsInvitation || _verifiedMobile is null)
        {
            throw new InvalidOperationException("Verified mobile context is unavailable.");
        }

        return CompleteMobileLoginAsync(invitation, ct);
    }

    public async Task<QrPollOutcome> PollQrOnceAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var code = _state.ActiveQrCode
            ?? throw new InvalidOperationException("QR challenge is unavailable.");
        var expiresAt = _state.QrExpiresAt
            ?? throw new InvalidOperationException("QR expiry is unavailable.");
        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            _state.MarkQrExpired();
            return QrPollOutcome.Expired;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            _state.ActiveToken, ct);
        var result = await _client.PollQrAsync(code, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            _state.MarkQrExpired();
            return QrPollOutcome.Expired;
        }

        _state.ApplyQrPoll(result);
        if (_state.Phase == LoginPhase.NeedsMobileBinding)
        {
            return QrPollOutcome.BindingRequired;
        }

        if (result.Session is not null && _state.Phase == LoginPhase.SignedIn)
        {
            await _saveLogin(result.Session, operation.Token);
            _state.CompleteSignIn();
            ClearSensitiveInput();
            return QrPollOutcome.SignedIn;
        }

        return QrPollOutcome.Continue;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearSensitiveInput();
    }

    private async Task<MobileSubmitOutcome> CompleteMobileLoginAsync(
        string? invitation,
        CancellationToken ct)
    {
        var verified = _verifiedMobile
            ?? throw new InvalidOperationException("Verified mobile context is unavailable.");
        var qrCode = _state.BindingQrCode;
        var operationToken = _state.BeginLogin(invitation);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(operationToken, ct);
        try
        {
            var session = qrCode is null
                ? await _client.LoginMobileAsync(
                    verified.Mobile, verified.SmsCode, invitation, operation.Token)
                : await _client.BindMobileAsync(
                    qrCode, verified.Mobile, verified.SmsCode, invitation, operation.Token);
            await _saveLogin(session, operation.Token);
            _state.CompleteSignIn();
            return MobileSubmitOutcome.SignedIn;
        }
        catch
        {
            _state.ResetMobileFailure();
            throw;
        }
        finally
        {
            ClearSensitiveInput();
        }
    }

    private void ClearSensitiveInput()
    {
        _verifiedMobile = null;
        if (_inputsCleared)
        {
            return;
        }

        _inputsCleared = true;
        _clearInputs();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
