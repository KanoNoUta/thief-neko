using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace CatapiController;

public partial class YodaChallengeWindow : Window
{
    private const string SeedUrl = "https://s0.meituan.net/mxx/yoda/yoda.seed.js";
    private string _requestCode;
    private readonly string _userDataFolder = Path.Combine(
        Path.GetTempPath(),
        $"thief-neko-yoda-{Guid.NewGuid():N}");
    private readonly YodaChallengeLifecycle _lifecycle;
    private bool _closeRequested;
    private bool _closeReady;

    internal YodaChallengeWindow(string requestCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestCode);
        _requestCode = requestCode;
        InitializeComponent();
        _lifecycle = new YodaChallengeLifecycle(
            InitializeChallengeAsync,
            DisposeWebView,
            CleanupProfileAsync);
        Loaded += async (_, _) =>
        {
            try
            {
                await _lifecycle.StartAsync();
            }
            catch when (!_lifecycle.LifetimeToken.IsCancellationRequested)
            {
                ChallengeStatusText.Text = "验证加载失败 / Challenge failed to load";
            }
        };
    }

    internal bool ChallengeSucceeded { get; private set; }

    private async Task InitializeChallengeAsync(CancellationToken ct)
    {
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: _userDataFolder);
        ct.ThrowIfCancellationRequested();
        await ChallengeWebView.EnsureCoreWebView2Async(environment);
        ct.ThrowIfCancellationRequested();
        ChallengeWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        ChallengeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        ChallengeWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        ChallengeWebView.CoreWebView2.WebMessageReceived += WebMessageReceived;
        ChallengeWebView.CoreWebView2.NavigationStarting += NavigationStarting;
        ChallengeWebView.NavigateToString(BuildChallengeHtml(_requestCode));
        ChallengeStatusText.Text = "请完成验证 / Complete the challenge";
    }

    private static void NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "slideValidationSuccess":
                    ChallengeSucceeded = true;
                    DialogResult = true;
                    break;
                case "slideValidationFail":
                    ChallengeSucceeded = false;
                    DialogResult = false;
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string BuildChallengeHtml(string requestCode)
    {
        var serializedRequestCode = JsonSerializer.Serialize(requestCode, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
        });
        return $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta http-equiv="Content-Security-Policy"
                    content="default-src 'none'; script-src https://s0.meituan.net 'unsafe-inline'; style-src 'unsafe-inline'; img-src https: data:; connect-src https:; frame-src https:">
              <style>
                html,body,#yoda-root{margin:0;width:100%;height:100%;background:#f5f5f1}
                body{display:flex;align-items:center;justify-content:center;font-family:Segoe UI,sans-serif}
              </style>
            </head>
            <body>
              <div id="yoda-root"></div>
              <script src="{{SeedUrl}}"></script>
              <script>
                (() => {
                  const send = type => window.chrome.webview.postMessage({ type });
                  const options = {
                    requestCode: {{serializedRequestCode}},
                    root: 'yoda-root',
                    successCallback: () => send('slideValidationSuccess'),
                    failCallback: () => send('slideValidationFail')
                  };
                  try {
                    if (typeof window.YodaSeed !== 'function') {
                      send('slideValidationFail');
                      return;
                    }
                    window.YodaSeed(options, 'slide');
                  } catch (_) {
                    send('slideValidationFail');
                  }
                })();
              </script>
            </body>
            </html>
            """;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ChallengeSucceeded = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady)
        {
            return;
        }

        e.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        try
        {
            await _lifecycle.CloseAsync();
        }
        finally
        {
            _requestCode = string.Empty;
            _closeReady = true;
            Close();
        }
    }

    private void DisposeWebView()
    {
        if (ChallengeWebView.CoreWebView2 is not null)
        {
            ChallengeWebView.CoreWebView2.WebMessageReceived -= WebMessageReceived;
            ChallengeWebView.CoreWebView2.NavigationStarting -= NavigationStarting;
        }

        ChallengeWebView.Dispose();
    }

    private async Task CleanupProfileAsync()
    {
        for (var attempt = 0; attempt < 40 && Directory.Exists(_userDataFolder); attempt++)
        {
            try
            {
                NormalizeAttributes(_userDataFolder);
                Directory.Delete(_userDataFolder, true);
            }
            catch (IOException) when (attempt < 39)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 39)
            {
                await Task.Delay(50);
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }
    }

    private static void NormalizeAttributes(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
