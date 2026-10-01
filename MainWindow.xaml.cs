using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using DesktopShell.NativeBackend;
using Microsoft.Web.WebView2.Core;

namespace DesktopShell;

public partial class MainWindow : Window
{
    private NativeBackendServer? _nativeBackend;
    private WebViewBridge? _webViewBridge;
    private bool _isClosing;
    private bool _windowIsDark;

    private bool _fullscreen;
    private WindowState _savedWindowState;
    private WindowStyle _savedWindowStyle;
    private ResizeMode _savedResizeMode;
    private Rect _savedBounds;
    public bool SetFullscreen(bool enabled)
    {
        if (enabled == _fullscreen) return _fullscreen;
        if (enabled)
        {
            _savedWindowState = WindowState; _savedWindowStyle = WindowStyle; _savedResizeMode = ResizeMode;
            _savedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal; WindowStyle = _savedWindowStyle; ResizeMode = _savedResizeMode;
            Left = _savedBounds.Left; Top = _savedBounds.Top; Width = _savedBounds.Width; Height = _savedBounds.Height;
            WindowState = _savedWindowState;
            ApplyWindowTheme(_windowIsDark);
        }
        _fullscreen = enabled;
        if (Browser.CoreWebView2 is not null)
            _ = Browser.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('jm-native-fullscreen',{detail:{fullscreen:" + (enabled ? "true" : "false") + "}}));");
        return _fullscreen;
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var systemIsDark = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int light && light == 0;
        ApplyWindowTheme(systemIsDark);
    }

    internal bool ApplyWindowTheme(bool dark)
    {
        _windowIsDark = dark;
        var highContrast = SystemParameters.HighContrast;
        var background = highContrast ? SystemColors.WindowColor
            : dark ? Color.FromRgb(17, 23, 19) : Color.FromRgb(245, 246, 244);
        Background = new SolidColorBrush(background);
        StartupPanel.Background = Background;
        Resources["StartupPrimaryText"] = highContrast ? SystemColors.WindowTextBrush
            : new SolidColorBrush(dark ? Color.FromRgb(237, 243, 238) : Color.FromRgb(32, 39, 33));
        Resources["StartupSecondaryText"] = highContrast ? SystemColors.WindowTextBrush
            : new SolidColorBrush(dark ? Color.FromRgb(178, 192, 182) : Color.FromRgb(80, 92, 83));
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, background.R, background.G, background.B);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        var immersiveDark = dark && !highContrast ? 1 : 0;
        var result = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref immersiveDark, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // COLORREF is 0x00BBGGRR; -1 restores Windows defaults in high-contrast mode.
            var captionColor = highContrast ? -1 : dark ? 0x00191E17 : 0x00F4F6F5;
            var textColor = highContrast ? -1 : dark ? 0x00EEF3ED : 0x00212720;
            DwmSetWindowAttribute(handle, DwmCaptionColor, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(handle, DwmTextColor, ref textColor, sizeof(int));
        }
        return result >= 0;
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
        ApplyWindowTheme(_windowIsDark);
        _webViewBridge = new WebViewBridge();
        await _webViewBridge.ConfigureAsync(Browser.CoreWebView2, token, this);

        StatusText.Text = "正在加载工作区...";
        DetailText.Text = "界面资源已就绪，正在建立本地连接";
        var navigationReady = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            => navigationReady.TrySetResult(args);

        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        Browser.Source = baseUri;
        var navigation = await navigationReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        if (!navigation.IsSuccess)
        {
            throw new InvalidOperationException($"界面加载失败：{navigation.WebErrorStatus}");
        }

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
