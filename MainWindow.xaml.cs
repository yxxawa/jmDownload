using System.ComponentModel;
using System.Windows;
using DesktopShell.NativeBackend;
using Microsoft.Web.WebView2.Core;

namespace DesktopShell;

public partial class MainWindow : Window
{
    private NativeBackendServer? _nativeBackend;
    private WebViewBridge? _webViewBridge;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await StartBackendAndLoadAsync();
    }

    private async Task StartBackendAndLoadAsync()
    {
        RetryButton.Visibility = Visibility.Collapsed;
        StartupProgress.Visibility = Visibility.Visible;
        StartupPanel.Visibility = Visibility.Visible;
        Browser.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在启动 C# 原生后端...";
        DetailText.Text = string.Empty;
        await DisposeBackendAsync();
        var progress = new Progress<string>(msg => { DetailText.Text = msg; });
        try
        {
            _nativeBackend = new NativeBackendServer();
            await _nativeBackend.StartAsync(progress);
            await InitializeWebViewAsync(_nativeBackend.BaseUri, _nativeBackend.Token);
        }
        catch (Exception ex)
        {
            StartupProgress.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
            StatusText.Text = "启动失败";
            DetailText.Text = ex.Message;
        }
    }

    private async Task InitializeWebViewAsync(Uri baseUri, string token)
    {
        StatusText.Text = "正在初始化 WebView2...";
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JMComicDesktop", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
        await Browser.EnsureCoreWebView2Async(env);
        _webViewBridge = new WebViewBridge();
        await _webViewBridge.ConfigureAsync(Browser.CoreWebView2, token, this);
        Browser.Source = baseUri;
        Browser.Visibility = Visibility.Visible;
        StartupPanel.Visibility = Visibility.Collapsed;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await StartBackendAndLoadAsync();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        Browser.CoreWebView2?.Stop();
        var nb = _nativeBackend; _nativeBackend = null;
        if (nb is not null) { nb.RequestShutdown(); _ = nb.DisposeAsync().AsTask(); }
    }

    private async Task DisposeBackendAsync()
    {
        if (_nativeBackend is not null) { await _nativeBackend.DisposeAsync(); _nativeBackend = null; }
    }
}
