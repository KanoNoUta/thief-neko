using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace CatapiController;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SettingsStore _settingsStore = new();
    private readonly AuthSessionStore _authSessionStore = new();
    private readonly HttpClient _authHttp = new();
    private readonly CatpawAuthClient _authClient;
    private readonly CatpawAuthService _authService;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Forms.NotifyIcon _trayIcon;
    private Process? _gatewayProcess;
    private int? _gatewayPid;
    private bool _ownsGateway;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isExiting;
    private bool _syncingToken;
    private bool _refreshing;
    private bool _updatingRange;
    private string _gatewayPath = "";
    private DateTime _rangeStart = DateTime.Today;
    private DateTime _rangeEnd = DateTime.Today;
    private CancellationTokenSource? _revealCancellation;
    private CredentialPipeServer? _credentialPipe;

    public MainWindow()
    {
        InitializeComponent();
        _authClient = new CatpawAuthClient(_authHttp, "5282fa6645");
        _authService = new CatpawAuthService(_authClient, _authSessionStore, "5282fa6645");
        AuthModeCombo.SelectedIndex = 0;
        ApplyRangePreset(1, refresh: false);
        _gatewayPath = LocateGatewayRoot();
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _trayIcon = CreateTrayIcon();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            if (settings is not null)
            {
                SetTokenValue(settings.Token);
                TenantTextBox.Text = settings.Tenant;
                AuthModeCombo.SelectedIndex = settings.AuthenticationMode switch
                {
                    AuthenticationMode.Headless => 0,
                    AuthenticationMode.FollowDesktop => 1,
                    _ => 2,
                };
                if (Directory.Exists(settings.GatewayPath))
                {
                    _gatewayPath = settings.GatewayPath;
                }
            }
        }
        catch (Exception error)
        {
            AddActivity($"配置读取失败 · {error.Message}");
        }

        await _authService.GetSessionAsync();
        UpdateAuthStatus();
        await RefreshStatusAsync();
        _pollTimer.Start();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add("启动 / 停止网关", null, async (_, _) => await Dispatcher.InvokeAsync(ServiceButton_ClickAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(ExitAsync));

        var icon = new Forms.NotifyIcon
        {
            Text = "Thief Neko",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
        return icon;
    }

    private async void ServiceButton_Click(object sender, RoutedEventArgs e) => await ServiceButton_ClickAsync();

    private async Task ServiceButton_ClickAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (_isRunning)
        {
            await StopGatewayAsync(false);
        }
        else
        {
            await SaveSettingsAsync();
            await StartGatewayAsync();
        }
    }

    private async void SaveRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在保存配置...");
            await SaveSettingsAsync();
            if (_isRunning)
            {
                await StopGatewayCoreAsync(false);
            }
            await StartGatewayCoreAsync();
        }
        catch (Exception error)
        {
            await CleanupGatewayCredentialsAsync();
            ShowError(error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = CurrentSettings();
        await _settingsStore.SaveAsync(settings);
        AddActivity("配置已加密保存");
    }

    private async Task StartGatewayAsync()
    {
        try
        {
            SetBusy(true, "正在启动网关...");
            await StartGatewayCoreAsync();
        }
        catch (Exception error)
        {
            await CleanupGatewayCredentialsAsync();
            ShowError(error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartGatewayCoreAsync()
    {
        if (await TryReadStatusAsync() is not null)
        {
            await RefreshStatusAsync();
            AddActivity("已连接现有网关");
            return;
        }
        if (await TryReadHealthAsync())
        {
            throw new InvalidOperationException("3000 端口上已有旧版网关，请先停止服务再启动。");
        }

        var settings = CurrentSettings();
        var session = settings.AuthenticationMode == AuthenticationMode.FollowDesktop
            ? await ReadCatpawSessionAsync(settings.GatewayPath)
            : null;
        if (settings.AuthenticationMode != AuthenticationMode.Headless)
        {
            var tokenResolution = TokenResolver.Resolve(settings, session);
            settings = tokenResolution.Settings;
            if (tokenResolution.Synced)
            {
                SetTokenValue(settings.Token);
                await _settingsStore.SaveAsync(settings);
                AddActivity("已自动同步 Catpaw Token");
            }
            else if (tokenResolution.UsedFallback)
            {
                AddActivity("自动获取失败，已使用手动 Token");
            }
        }
        else
        {
            if (await _authService.GetSessionAsync() is null)
            {
                throw new InvalidOperationException("Login to Catpaw before starting the headless gateway.");
            }
            await _authService.StartAsync();
            _credentialPipe = new CredentialPipeServer(_authService);
            await _credentialPipe.StartAsync();
        }

        var startInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = settings.GatewayPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("src/server.js");
        GatewayLaunchEnvironment.Apply(startInfo, settings, session?.UserMis ?? "",
            _credentialPipe?.PipeName, _credentialPipe?.Nonce);

        _gatewayProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _gatewayProcess.OutputDataReceived += (_, e) => RecordProcessLine(e.Data);
        _gatewayProcess.ErrorDataReceived += (_, e) => RecordProcessLine(e.Data);
        if (!_gatewayProcess.Start())
        {
            throw new InvalidOperationException("无法启动 Node 网关。");
        }
        _gatewayProcess.BeginOutputReadLine();
        _gatewayProcess.BeginErrorReadLine();
        _gatewayPid = _gatewayProcess.Id;
        _ownsGateway = true;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(500);
            if (await TryReadStatusAsync() is not null)
            {
                AddActivity($"网关已启动 · PID {_gatewayPid}");
                await RefreshStatusAsync();
                return;
            }
            if (_gatewayProcess.HasExited)
            {
                break;
            }
        }

        await StopGatewayCoreAsync(true);
        throw new InvalidOperationException("网关启动超时，请检查 Node.js 和配置。 ");
    }

    private async Task StopGatewayAsync(bool ownedOnly)
    {
        try
        {
            SetBusy(true, "正在停止网关...");
            await StopGatewayCoreAsync(ownedOnly);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StopGatewayCoreAsync(bool ownedOnly)
    {
        if (ownedOnly && !_ownsGateway)
        {
            await CleanupGatewayCredentialsAsync();
            return;
        }

        var pid = _gatewayProcess is { HasExited: false } ? _gatewayProcess.Id : _gatewayPid;
        if (pid is not null)
        {
            try
            {
                var process = Process.GetProcessById(pid.Value);
                if (!_ownsGateway && !string.Equals(process.ProcessName, "node", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("端口 3000 不是由 Node 网关占用，未执行停止操作。");
                }
                process.Kill(true);
                await process.WaitForExitAsync();
            }
            catch (ArgumentException)
            {
            }
        }

        _gatewayProcess?.Dispose();
        _gatewayProcess = null;
        _gatewayPid = null;
        _ownsGateway = false;
        await CleanupGatewayCredentialsAsync();
        SetRunning(false);
        AddActivity("网关已停止");
    }

    private async Task CleanupGatewayCredentialsAsync()
    {
        if (_credentialPipe is not null)
        {
            await _credentialPipe.DisposeAsync();
            _credentialPipe = null;
        }
        await _authService.StopAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            using var document = await TryReadStatusAsync();
            if (document is null)
            {
                if (await TryReadHealthAsync())
                {
                    _gatewayPid = await FindPortOwnerPidAsync();
                    SetRunning(true);
                    ServiceStateText.Text = "运行中（旧版）";
                    FooterText.Text = "旧网关不支持状态统计，停止后重新启动即可升级";
                    if (_gatewayPid is not null)
                    {
                        try
                        {
                            var process = Process.GetProcessById(_gatewayPid.Value);
                            MemoryText.Text = $"{process.WorkingSet64 / 1024d / 1024d:0} MB";
                            MemoryDetailText.Text = "旧版进程";
                        }
                        catch
                        {
                        }
                    }
                    return;
                }
                SetRunning(false);
                return;
            }

            var root = document.RootElement;
            _gatewayPid = ReadInt32(root, "pid");
            SetRunning(true);
            EndpointText.Text = $"127.0.0.1:3000  ·  {ReadString(root, "model") ?? "glm-5.2"}";

            var quota = root.GetProperty("quota");
            var remaining = ReadNumber(quota, "remaining");
            var used = ReadNumber(quota, "used");
            QuotaText.Text = FormatCount(remaining);
            QuotaDetailText.Text = used is null ? "暂无数据" : $"已使用 {FormatCount(used)}";

            var usage = root.GetProperty("usage");
            InputTokenText.Text = FormatTokenMillions(ReadNumber(usage, "inputTokens"));
            OutputTokenText.Text = FormatTokenMillions(ReadNumber(usage, "outputTokens"));

            var rss = ReadNumber(root.GetProperty("memory"), "rssBytes");
            if (rss is not null)
            {
                var megabytes = rss.Value / 1024d / 1024d;
                MemoryText.Text = $"{megabytes:0} MB";
                MemoryDetailText.Text = megabytes >= 512 ? "过高" : megabytes >= 256 ? "偏高" : "稳定";
                MemoryDetailText.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(
                    megabytes >= 256 ? "#D6A45E" : "#6EB27C"));
            }

            UpdateRemoteActivity(root);
        }
        catch
        {
            SetRunning(false);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task<JsonDocument?> TryReadStatusAsync()
    {
        try
        {
            var statusUri = new Uri(
                $"http://127.0.0.1:3000/admin/status?start={_rangeStart:yyyy-MM-dd}&end={_rangeEnd:yyyy-MM-dd}");
            var json = await _http.GetStringAsync(statusUri);
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private void RangePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button
            || !int.TryParse(button.Tag?.ToString(), out var days))
        {
            return;
        }

        if (days == 0)
        {
            UpdateRangeButtons(RangeCustomButton);
            return;
        }
        ApplyRangePreset(days, refresh: true);
    }

    private void ApplyRangePreset(int days, bool refresh)
    {
        _updatingRange = true;
        _rangeEnd = DateTime.Today;
        _rangeStart = _rangeEnd.AddDays(1 - days);
        StartDatePicker.SelectedDate = _rangeStart;
        EndDatePicker.SelectedDate = _rangeEnd;
        _updatingRange = false;

        var selected = days switch
        {
            7 => Range7Button,
            30 => Range30Button,
            _ => Range1Button,
        };
        UpdateRangeButtons(selected);
        ShowRangeInFooter();
        if (refresh && IsLoaded)
        {
            _ = RefreshStatusAsync();
        }
    }

    private void DateRange_SelectedDateChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingRange
            || StartDatePicker.SelectedDate is null
            || EndDatePicker.SelectedDate is null)
        {
            return;
        }

        var start = StartDatePicker.SelectedDate.Value.Date;
        var end = EndDatePicker.SelectedDate.Value.Date;
        if (end < start)
        {
            FooterText.Text = "结束日期不能早于开始日期";
            FooterText.Foreground = new SolidColorBrush(
                (MediaColor)MediaColorConverter.ConvertFromString("#D6A45E"));
            return;
        }
        if ((end - start).TotalDays + 1 > 731)
        {
            FooterText.Text = "统计范围不能超过两年";
            FooterText.Foreground = new SolidColorBrush(
                (MediaColor)MediaColorConverter.ConvertFromString("#D6A45E"));
            return;
        }

        _rangeStart = start;
        _rangeEnd = end;
        UpdateRangeButtons(RangeCustomButton);
        ShowRangeInFooter();
        if (IsLoaded)
        {
            _ = RefreshStatusAsync();
        }
    }

    private void UpdateRangeButtons(System.Windows.Controls.Button selected)
    {
        var normalBackground = new SolidColorBrush(
            (MediaColor)MediaColorConverter.ConvertFromString("#292922"));
        var normalForeground = new SolidColorBrush(
            (MediaColor)MediaColorConverter.ConvertFromString("#D4D2C7"));
        var selectedBackground = new SolidColorBrush(
            (MediaColor)MediaColorConverter.ConvertFromString("#D97757"));
        var selectedForeground = new SolidColorBrush(
            (MediaColor)MediaColorConverter.ConvertFromString("#211815"));

        foreach (var button in new[] { Range1Button, Range7Button, Range30Button, RangeCustomButton })
        {
            button.Background = button == selected ? selectedBackground : normalBackground;
            button.Foreground = button == selected ? selectedForeground : normalForeground;
        }
    }

    private void ShowRangeInFooter()
    {
        FooterText.Text = $"统计范围：{_rangeStart:yyyy-MM-dd} 至 {_rangeEnd:yyyy-MM-dd}";
        FooterText.Foreground = new SolidColorBrush(
            (MediaColor)MediaColorConverter.ConvertFromString("#77766E"));
    }

    private async Task<bool> TryReadHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync("http://127.0.0.1:3000/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int?> FindPortOwnerPidAsync()
    {
        try
        {
            var info = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("(Get-NetTCPConnection -State Listen -LocalPort 3000 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess)");
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return int.TryParse(output.Trim(), out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateRemoteActivity(JsonElement root)
    {
        if (!root.TryGetProperty("recentActivity", out var activity) || activity.GetArrayLength() == 0)
        {
            return;
        }

        ActivityList.Items.Clear();
        foreach (var item in activity.EnumerateArray().Take(5))
        {
            var at = DateTime.TryParse(ReadString(item, "at"), out var time)
                ? time.ToLocalTime().ToString("HH:mm:ss")
                : "--:--:--";
            ActivityList.Items.Add($"{at}  {ReadString(item, "type")}  ·  {ReadNumber(item, "status")}");
        }
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        ServiceStateText.Text = running ? "运行中" : "已停止";
        ServiceButton.Content = running ? "停止服务" : "启动服务";
        StatusDot.Fill = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(running ? "#6EB27C" : "#77756D"));
        if (!running)
        {
            MemoryText.Text = "暂无数据";
            MemoryDetailText.Text = "未运行";
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        ServiceButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        if (message is not null)
        {
            FooterText.Text = message;
        }
        else if (!busy)
        {
            FooterText.Text = "配置保存在本机用户加密存储中";
            FooterText.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString("#77766E"));
        }
    }

    private void ShowError(string message)
    {
        FooterText.Text = message;
        FooterText.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString("#D6A45E"));
        AddActivity($"错误 · {message}");
    }

    private void AddActivity(string message)
    {
        message = ControllerPresentation.RedactActivity(message);
        Dispatcher.Invoke(() =>
        {
            ActivityList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (ActivityList.Items.Count > 6)
            {
                ActivityList.Items.RemoveAt(ActivityList.Items.Count - 1);
            }
        });
    }

    private void RecordProcessLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            AddActivity(line.Length > 90 ? line[..90] + "..." : line);
        }
    }

    private ControllerSettings CurrentSettings() => new(
        TokenPasswordBox.Password.Trim(),
        TenantTextBox.Text.Trim(),
        _gatewayPath,
        AuthModeCombo.SelectedIndex switch
        {
            0 => AuthenticationMode.Headless,
            1 => AuthenticationMode.FollowDesktop,
            _ => AuthenticationMode.Manual,
        });

    private void AuthModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var manual = AuthModeCombo.SelectedIndex == 2;
        TokenPasswordBox.IsEnabled = manual;
        TokenRevealBox.IsEnabled = manual;
        RevealButton.IsEnabled = manual;
    }

    private async void LoginCatpaw_Click(object sender, RoutedEventArgs e)
    {
        var window = new LoginWindow(_authClient, _authService) { Owner = this };
        window.ShowDialog();
        UpdateAuthStatus();
        if (_authService.GetStatus().SignedIn)
        {
            AuthModeCombo.SelectedIndex = 0;
            await SaveSettingsAsync();
        }
    }

    private async void ImportLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Importing Catpaw login...");
            await _authService.ImportDesktopSessionAsync(_gatewayPath, CancellationToken.None);
            AuthModeCombo.SelectedIndex = 0;
            UpdateAuthStatus();
            await SaveSettingsAsync();
            AddActivity("Catpaw login imported");
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { SetBusy(false); }
    }

    private void UpdateAuthStatus() => AuthStatusText.Text =
        ControllerPresentation.FormatAuthStatus(_authService.GetStatus());

    private static async Task<CatpawSession?> ReadCatpawSessionAsync(string gatewayPath)
    {
        try
        {
            var info = new ProcessStartInfo("node")
            {
                WorkingDirectory = gatewayPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            info.ArgumentList.Add("src/catpawState.js");
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            using var document = JsonDocument.Parse(output);
            var token = ReadString(document.RootElement, "token");
            var userMis = ReadString(document.RootElement, "userMis");
            return string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userMis)
                ? null
                : new CatpawSession(token, userMis);
        }
        catch
        {
            return null;
        }
    }

    private void SetTokenValue(string token)
    {
        _syncingToken = true;
        TokenPasswordBox.Password = token;
        TokenRevealBox.Text = token;
        _syncingToken = false;
    }

    private static string LocateGatewayRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 9; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "server.js")))
                {
                    return directory.FullName;
                }
            }
        }
        return Environment.CurrentDirectory;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? ReadInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string FormatCount(double? value)
    {
        if (value is null)
        {
            return "暂无数据";
        }
        return $"{value:0.##}";
    }

    private static string FormatTokenMillions(double? value)
    {
        if (value is null)
        {
            return "暂无数据";
        }
        return $"{value / 1_000_000d:0.######} M";
    }

    private async void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (TokenRevealBox.Visibility == Visibility.Visible)
        {
            HideToken();
            return;
        }

        _syncingToken = true;
        TokenRevealBox.Text = TokenPasswordBox.Password;
        TokenRevealBox.Visibility = Visibility.Visible;
        TokenPasswordBox.Visibility = Visibility.Collapsed;
        RevealButton.Content = "隐藏 Token";
        _syncingToken = false;

        _revealCancellation?.Cancel();
        _revealCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _revealCancellation.Token);
            HideToken();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HideToken()
    {
        _syncingToken = true;
        TokenPasswordBox.Password = TokenRevealBox.Text;
        TokenRevealBox.Visibility = Visibility.Collapsed;
        TokenPasswordBox.Visibility = Visibility.Visible;
        RevealButton.Content = "显示 Token";
        _syncingToken = false;
    }

    private void TokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingToken)
        {
            TokenRevealBox.Text = TokenPasswordBox.Password;
        }
    }

    private void TokenRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_syncingToken)
        {
            TokenPasswordBox.Password = TokenRevealBox.Text;
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private async Task ExitAsync()
    {
        _isExiting = true;
        _pollTimer.Stop();
        _revealCancellation?.Cancel();
        await StopGatewayCoreAsync(true);
        await _authService.DisposeAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _http.Dispose();
        _authHttp.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
