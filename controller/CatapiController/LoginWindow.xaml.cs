using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CatapiController;

public partial class LoginWindow : Window
{
    private readonly ICatpawLoginClient _client;
    private readonly CatpawAuthService _authService;
    private readonly LoginStateMachine _state = new();
    private readonly DispatcherTimer _countdownTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private Task? _pollTask;
    private bool _closing;
    private string? _statusOverride;

    internal LoginWindow(ICatpawLoginClient client, CatpawAuthService authService)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        InitializeComponent();
        _countdownTimer.Tick += CountdownTimer_Tick;
        Loaded += async (_, _) => await BeginQrFlowAsync();
    }

    private async Task BeginQrFlowAsync()
    {
        _statusOverride = null;
        var token = _state.BeginQrRequest();
        QrTermsCheckBox.IsChecked = false;
        QrImage.Source = null;
        RenderState();
        try
        {
            var challenge = await _client.CreateQrAsync(token);
            _state.QrReceived(challenge);
            ShowQrImage(challenge.QrCodeUrl);
            RenderState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            _state.Fail();
            _statusOverride = "二维码请求失败 / QR request failed";
            RenderState();
        }
    }

    private void StartQrPolling()
    {
        if (_pollTask is { IsCompleted: false } || !_state.TermsAccepted ||
            _state.QrExpiresAt is null)
        {
            return;
        }

        _pollTask = PollQrAsync(_state.ActiveToken);
    }

    private async Task PollQrAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested &&
                   _state.QrExpiresAt is { } expiresAt && DateTimeOffset.UtcNow < expiresAt)
            {
                var challengeCode = GetActiveQrCode();
                var result = await _client.PollQrAsync(challengeCode, token);
                _state.ApplyQrPoll(result);
                RenderState();
                if (_state.Phase == LoginPhase.NeedsMobileBinding)
                {
                    MobileModeRadio.IsChecked = true;
                    return;
                }

                if (result.Session is not null && _state.Phase == LoginPhase.SignedIn)
                {
                    await FinishSignInAsync(result.Session, token);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }

            if (!token.IsCancellationRequested && _state.Phase is
                LoginPhase.WaitingForScan or LoginPhase.Scanned)
            {
                _state.MarkQrExpired();
                RenderState();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            if (!_closing)
            {
                _state.Fail();
                _statusOverride = "轮询失败，请重试 / Polling failed, retry";
                RenderState();
            }
        }
    }

    private string GetActiveQrCode()
    {
        return _state.ActiveQrCode
            ?? throw new InvalidOperationException("QR challenge is unavailable.");
    }

    private async void QrTerms_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _state.Mode != LoginMode.Qr)
        {
            return;
        }

        _state.SetTermsAccepted(QrTermsCheckBox.IsChecked == true);
        RenderState();
        if (_state.TermsAccepted)
        {
            StartQrPolling();
            await Task.Yield();
        }
    }

    private async void RetryQr_Click(object sender, RoutedEventArgs e) => await BeginQrFlowAsync();

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_state.QrImageUrl is not { } url)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            _statusOverride = "无法打开浏览器 / Browser unavailable";
            RenderState();
            return;
        }

        if (_state.Phase is LoginPhase.WaitingForScan or LoginPhase.Scanned)
        {
            StartQrPolling();
        }
    }

    private async void QrMode_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _state.Mode == LoginMode.Qr)
        {
            return;
        }

        ClearMobileInputs();
        await BeginQrFlowAsync();
    }

    private void MobileMode_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _state.Mode == LoginMode.Mobile)
        {
            return;
        }

        _state.SwitchMode(LoginMode.Mobile);
        MobileTermsCheckBox.IsChecked = false;
        RenderState();
    }

    private void MobileTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _state.SetMobile(MobileTextBox.Text);
        RenderState();
    }

    private void SmsCodeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RenderState();

    private void InvitationTextBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e) => RenderState();

    private void MobileTerms_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _state.Mode != LoginMode.Mobile)
        {
            return;
        }

        _state.SetTermsAccepted(MobileTermsCheckBox.IsChecked == true);
        RenderState();
    }

    private async void SendSms_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.CanSendSms)
        {
            _statusOverride = "请检查手机号并同意条款 / Check mobile and terms";
            RenderState();
            return;
        }

        await SendSmsCoreAsync();
    }

    private async Task SendSmsCoreAsync()
    {
        _statusOverride = null;
        var mobile = MobileTextBox.Text;
        var token = _state.Phase == LoginPhase.SendingSms
            ? _state.ActiveToken
            : _state.BeginSmsRequest(mobile);
        RenderState();
        try
        {
            var installationId = await _authService.GetInstallationIdAsync(token);
            while (true)
            {
                var challenge = await _client.SendSmsAsync(mobile, installationId, token);
                _state.SmsSent(challenge);
                RenderState();
                if (string.IsNullOrWhiteSpace(challenge.RequestCode))
                {
                    _countdownTimer.Start();
                    return;
                }

                var yoda = new YodaChallengeWindow(challenge.RequestCode) { Owner = this };
                var succeeded = yoda.ShowDialog() == true && yoda.ChallengeSucceeded;
                _state.ApplyYodaResult(succeeded);
                RenderState();
                if (!succeeded)
                {
                    _statusOverride = "验证未通过，请重试 / Challenge failed";
                    RenderState();
                    return;
                }

                token = _state.ActiveToken;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            _state.ResetMobileFailure();
            _statusOverride = "短信发送失败，请重试 / SMS request failed";
            RenderState();
        }
    }

    private async void SubmitMobile_Click(object sender, RoutedEventArgs e)
    {
        var mobile = MobileTextBox.Text;
        var smsCode = SmsCodeTextBox.Text;
        var invitation = InvitationPanel.Visibility == Visibility.Visible
            ? InvitationTextBox.Text
            : null;

        if (!LoginStateMachine.IsValidChineseMobile(mobile) ||
            !LoginStateMachine.IsValidSmsCode(smsCode) ||
            !_state.TermsAccepted ||
            (_state.Phase == LoginPhase.NeedsInvitation &&
             !LoginStateMachine.IsValidInvitation(invitation)))
        {
            _statusOverride = "请检查输入 / Check the form";
            RenderState();
            return;
        }

        try
        {
            var token = _state.BeginVerification(mobile, smsCode);
            RenderState();
            var verification = await _client.VerifySmsAsync(mobile, smsCode, token);
            _state.ApplyVerification(verification);
            RenderState();
            if (!verification.Verified)
            {
                _statusOverride = "验证码无效 / Invalid SMS code";
                RenderState();
                return;
            }

            if (verification.InvitationCodeRequired &&
                !LoginStateMachine.IsValidInvitation(invitation))
            {
                InvitationTextBox.Focus();
                return;
            }

            await SubmitLoginAsync(mobile, smsCode, invitation);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _state.ResetMobileFailure();
            _statusOverride = "登录失败，请重试 / Sign-in failed";
            RenderState();
        }
    }

    private async Task SubmitLoginAsync(string mobile, string smsCode, string? invitation)
    {
        var qrCode = _state.BindingQrCode;
        var token = _state.BeginLogin(invitation);
        RenderState();
        var session = qrCode is null
            ? await _client.LoginMobileAsync(mobile, smsCode, invitation, token)
            : await _client.BindMobileAsync(qrCode, mobile, smsCode, invitation, token);
        await FinishSignInAsync(session, token);
    }

    private async Task FinishSignInAsync(AuthSession session, CancellationToken token)
    {
        await _authService.SaveLoginAsync(session, token);
        _state.CompleteSignIn();
        ClearMobileInputs();
        DialogResult = true;
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _state.TickCountdown();
        if (_state.CountdownSeconds == 0)
        {
            _countdownTimer.Stop();
        }

        RenderState();
    }

    private void RenderState()
    {
        if (!IsInitialized || _closing)
        {
            return;
        }

        var mobile = _state.Mode == LoginMode.Mobile;
        QrPanel.Visibility = mobile ? Visibility.Collapsed : Visibility.Visible;
        MobilePanel.Visibility = mobile ? Visibility.Visible : Visibility.Collapsed;
        QrModeRadio.IsChecked = !mobile;
        MobileModeRadio.IsChecked = mobile;
        BindingNotice.Visibility = _state.IsBinding ? Visibility.Visible : Visibility.Collapsed;
        MobileHeading.Text = _state.IsBinding
            ? "绑定手机号 / Bind mobile"
            : "手机号登录 / Mobile login";

        QrPlaceholder.Visibility = QrImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        QrTermsCheckBox.IsEnabled = _state.Phase == LoginPhase.WaitingForAgreement;
        OpenBrowserButton.IsEnabled = _state.QrImageUrl is not null;
        RetryQrButton.Visibility = _state.Phase is LoginPhase.Expired or LoginPhase.Failed
            ? Visibility.Visible
            : Visibility.Hidden;
        System.Windows.Controls.Grid.SetColumnSpan(
            OpenBrowserButton,
            RetryQrButton.Visibility == Visibility.Visible ? 1 : 3);
        QrStateText.Text = _state.Phase switch
        {
            LoginPhase.RequestingQr => "正在请求二维码 / Requesting QR",
            LoginPhase.WaitingForAgreement => "同意条款后开始扫码 / Accept terms to start",
            LoginPhase.WaitingForScan => "请使用 Catpaw 扫码 / Scan with Catpaw",
            LoginPhase.Scanned => "已扫码，等待确认 / Scanned, confirm in Catpaw",
            LoginPhase.Expired => "二维码已过期 / QR expired",
            LoginPhase.Failed => "二维码流程失败 / QR flow failed",
            _ => "准备二维码 / QR ready",
        };

        SendSmsButton.IsEnabled = _state.CanSendSms;
        SendSmsButton.Content = _state.CountdownSeconds > 0
            ? $"{_state.CountdownSeconds}s"
            : "发送验证码";
        InvitationPanel.Visibility = _state.Phase == LoginPhase.NeedsInvitation
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubmitMobileButton.IsEnabled =
            LoginStateMachine.IsValidChineseMobile(MobileTextBox.Text) &&
            LoginStateMachine.IsValidSmsCode(SmsCodeTextBox.Text) &&
            _state.TermsAccepted &&
            (_state.Phase != LoginPhase.NeedsInvitation ||
             LoginStateMachine.IsValidInvitation(InvitationTextBox.Text)) &&
            _state.Phase is not LoginPhase.SendingSms and not LoginPhase.VerifyingSms and
                not LoginPhase.SigningIn and not LoginPhase.YodaChallenge;
        SubmitMobileButton.Content = _state.IsBinding
            ? "绑定并登录 / Bind & sign in"
            : "登录 / Sign in";

        StatusText.Text = _statusOverride ?? _state.Phase switch
        {
            LoginPhase.SendingSms => "正在发送短信 / Sending SMS",
            LoginPhase.YodaChallenge => "安全验证 / Security challenge",
            LoginPhase.WaitingForSmsCode => "验证码已发送 / SMS sent",
            LoginPhase.VerifyingSms => "正在验证 / Verifying",
            LoginPhase.NeedsInvitation => "需要六位大写邀请码 / Invitation required",
            LoginPhase.SigningIn => "正在登录 / Signing in",
            LoginPhase.NeedsMobileBinding => "完成手机号绑定后继续 / Bind mobile to continue",
            _ => "凭据仅保存在本机加密存储 / Encrypted local storage only",
        };
    }

    private void ShowQrImage(string url)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(url, UriKind.Absolute);
        image.DecodePixelWidth = 232;
        image.CacheOption = BitmapCacheOption.OnDemand;
        image.EndInit();
        QrImage.Source = image;
    }

    private void ClearMobileInputs()
    {
        MobileTextBox.Clear();
        SmsCodeTextBox.Clear();
        InvitationTextBox.Clear();
        MobileTermsCheckBox.IsChecked = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _countdownTimer.Stop();
        ClearMobileInputs();
        _state.Dispose();
        QrImage.Source = null;
    }
}
