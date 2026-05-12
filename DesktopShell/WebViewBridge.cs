using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DesktopShell;

public sealed class WebViewBridge
{
    private DesktopBridge? _desktopBridge;

    public async Task ConfigureAsync(CoreWebView2 webView, string token, Window owner)
    {
        webView.Settings.AreDefaultContextMenusEnabled = true;
        webView.Settings.AreDevToolsEnabled = true;
        webView.Settings.IsStatusBarEnabled = false;

        await webView.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__JMDOWNLOAD_TOKEN__ = " + System.Text.Json.JsonSerializer.Serialize(token) + ";" +
            "window.__JMDOWNLOAD_DESKTOP__ = !!window.chrome?.webview;");

        webView.WebResourceRequested += (_, args) =>
        {
            var uri = new Uri(args.Request.Uri);
            if (uri.Host is "127.0.0.1" or "localhost")
            {
                args.Request.Headers.SetHeader("Authorization", "Bearer " + token);
            }
        };

        webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _desktopBridge = new DesktopBridge(webView, owner);
    }
}
