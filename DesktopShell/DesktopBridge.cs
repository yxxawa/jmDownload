using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DesktopShell;

public sealed class DesktopBridge
{
    private readonly CoreWebView2 _webView;
    private readonly Window _owner;

    public DesktopBridge(CoreWebView2 webView, Window owner)
    {
        _webView = webView;
        _owner = owner;
        _webView.WebMessageReceived += OnWebMessageReceived;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        BridgeRequest? request = null;

        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson);
            if (request is null || string.IsNullOrWhiteSpace(request.Id))
            {
                return;
            }

            var result = request.Type switch
            {
                "selectDirectory" => SelectDirectory(request.Payload, _owner),
                "openDirectory" => OpenDirectory(request.Payload),
                _ => BridgeResult.Fail("unsupported bridge command: " + request.Type),
            };

            await SendResponseAsync(request.Id, result);
        }
        catch (Exception ex)
        {
            if (request is not null)
            {
                await SendResponseAsync(request.Id, BridgeResult.Fail(ex.Message));
            }
        }
    }

    private static BridgeResult SelectDirectory(JsonElement payload, Window owner)
    {
        var initialDirectory = GetPayloadString(payload, "path");

        var dialog = new OpenFolderDialog
        {
            Title = "选择下载目录",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var result = ownerHandle == IntPtr.Zero
            ? dialog.ShowDialog()
            : dialog.ShowDialog(owner);
        if (result != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return BridgeResult.Ok(new { cancelled = true });
        }

        return BridgeResult.Ok(new { cancelled = false, path = dialog.FolderName });
    }

    private static BridgeResult OpenDirectory(JsonElement payload)
    {
        var path = GetPayloadString(payload, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return BridgeResult.Fail("路径为空");
        }

        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });

        return BridgeResult.Ok(new { path });
    }

    private async Task SendResponseAsync(string requestId, BridgeResult result)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = requestId,
            ok = result.Success,
            data = result.Data,
            error = result.Error,
        });

        await _webView.ExecuteScriptAsync(
            "window.dispatchEvent(new CustomEvent('jm-desktop-response', { detail: " + payload + " }));");
    }

    private static string GetPayloadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private sealed class BridgeRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }
    }

    private sealed class BridgeResult
    {
        public bool Success { get; private init; }
        public object? Data { get; private init; }
        public string? Error { get; private init; }

        public static BridgeResult Ok(object data) => new()
        {
            Success = true,
            Data = data,
        };

        public static BridgeResult Fail(string error) => new()
        {
            Success = false,
            Error = error,
        };
    }
}
