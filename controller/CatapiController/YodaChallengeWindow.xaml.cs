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

    internal YodaChallengeWindow(string requestCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestCode);
        _requestCode = requestCode;
        InitializeComponent();
        Loaded += async (_, _) => await InitializeChallengeAsync();
    }

    internal bool ChallengeSucceeded { get; private set; }

    private async Task InitializeChallengeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolder);
            await ChallengeWebView.EnsureCoreWebView2Async(environment);
            ChallengeWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ChallengeWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ChallengeWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ChallengeWebView.CoreWebView2.WebMessageReceived += WebMessageReceived;
            ChallengeWebView.CoreWebView2.NavigationStarting += NavigationStarting;
            ChallengeWebView.NavigateToString(BuildChallengeHtml(_requestCode));
            ChallengeStatusText.Text = "请完成验证 / Complete the challenge";
        }
        catch
        {
            ChallengeStatusText.Text = "验证加载失败 / Challenge failed to load";
        }
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
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        if (ChallengeWebView.CoreWebView2 is not null)
        {
            ChallengeWebView.CoreWebView2.WebMessageReceived -= WebMessageReceived;
            ChallengeWebView.CoreWebView2.NavigationStarting -= NavigationStarting;
        }

        _requestCode = string.Empty;
        ChallengeWebView.Dispose();
        for (var attempt = 0; attempt < 5 && Directory.Exists(_userDataFolder); attempt++)
        {
            try
            {
                Directory.Delete(_userDataFolder, true);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
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
}
