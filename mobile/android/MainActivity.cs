using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using System.Text;
using System.Text.Json;

namespace JmDownload.Mobile;

[Activity(
    MainLauncher = true,
    Exported = true,
    Label = "@string/app_name",
    Theme = "@style/AppTheme",
    HardwareAccelerated = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.KeyboardHidden | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Density)]
public class MainActivity : Activity
{
    private const int StorageRequestCode = 4101;
    private const int NotificationRequestCode = 4102;

    private static readonly Color LightBackground = Color.ParseColor("#F4F6F3");
    private static readonly Color DarkBackground = Color.ParseColor("#10150F");

    private FrameLayout? _root;
    private WebView? _webView;
    private View? _splash;
    private TextView? _splashText;
    private int _insetLeft = -1, _insetTop = -1, _insetRight = -1, _insetBottom = -1;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildInterface();
        ApplySystemBars(!IsSystemDark());
        if (!BackendServiceRunning()) StartBackendService();
        RequestNotificationPermission();
        _ = LoadWhenReadyAsync();
    }

    protected override void OnDestroy()
    {
        _webView?.StopLoading();
        _webView?.Destroy();
        _webView = null;
        base.OnDestroy();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        _webView?.BringToFront();
    }

    /* ───────────────────────── 界面 ───────────────────────── */

    private void BuildInterface()
    {
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(LightBackground);

        _webView = new WebView(this);
        var settings = _webView.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.DatabaseEnabled = true;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = false;
        settings.SetSupportZoom(false);
        settings.BuiltInZoomControls = false;
        settings.DisplayZoomControls = false;
        settings.UseWideViewPort = false;
        settings.LoadWithOverviewMode = false;
        settings.CacheMode = CacheModes.Default;
        settings.MediaPlaybackRequiresUserGesture = false;
        settings.SetGeolocationEnabled(false);
        settings.TextZoom = 100;
        _webView.SetBackgroundColor(Color.Transparent);
        _webView.HapticFeedbackEnabled = false;
        _webView.SetWebViewClient(new LocalWebViewClient(this));
        _webView.SetWebChromeClient(new WebChromeClient());

        _root.AddView(_webView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _root.AddView(BuildSplash(), new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _root.SetOnApplyWindowInsetsListener(new RootInsetsListener(this));

        SetContentView(_root);
    }

    private View BuildSplash()
    {
        var stack = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        stack.SetGravity(GravityFlags.Center);
        stack.SetBackgroundColor(LightBackground);

        var mark = new ImageView(this);
        mark.SetImageResource(Resource.Mipmap.ic_launcher);
        var markSize = (int)(72 * Resources!.DisplayMetrics!.Density);
        var markParams = new LinearLayout.LayoutParams(markSize, markSize);
        stack.AddView(mark, markParams);

        _splashText = new TextView(this) { Text = "正在启动本地服务…" };
        _splashText.SetTextColor(Color.ParseColor("#4E5D55"));
        _splashText.SetTextSize(Android.Util.ComplexUnitType.Sp, 13.5f);
        var textParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        textParams.TopMargin = (int)(18 * Resources.DisplayMetrics.Density);
        stack.AddView(_splashText, textParams);

        var progress = new ProgressBar(this) { Indeterminate = true };
        var progressParams = new LinearLayout.LayoutParams(
            (int)(26 * Resources.DisplayMetrics.Density), (int)(26 * Resources.DisplayMetrics.Density));
        progressParams.TopMargin = (int)(14 * Resources.DisplayMetrics.Density);
        stack.AddView(progress, progressParams);

        _splash = stack;
        return stack;
    }

    private void HideSplash()
    {
        var splash = _splash;
        if (splash is null || splash.Alpha < 1f && splash.Visibility != ViewStates.Visible) return;
        _splash = null;
        splash.Animate()?.Alpha(0f)?.SetDuration(280)?.WithEndAction(new Java.Lang.Runnable(() =>
        {
            splash.Visibility = ViewStates.Gone;
            (_root as ViewGroup)?.RemoveView(splash);
        }))?.Start();
    }

    /* ───────────────────────── 生命周期 ───────────────────────── */

    private bool BackendServiceRunning() => BackendServiceToggle.Running;

    private void StartBackendService()
    {
        var intent = new Intent(this, typeof(BackendService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(intent);
        else StartService(intent);
        BackendServiceToggle.Running = true;
    }

    private async Task LoadWhenReadyAsync()
    {
        try
        {
            await BackendHost.StartAsync(this).ConfigureAwait(false);
            RunOnUiThread(() => _webView?.LoadUrl(BackendHost.BaseUrl + "/"));
        }
        catch (Exception exception)
        {
            RunOnUiThread(() => ShowFailure(exception.Message));
        }
    }

    private void ShowFailure(string message)
    {
        _splashText!.Text = "本地服务启动失败\n" + message;
        _splashText.SetTextColor(Color.ParseColor("#C84C4C"));
    }

    internal void OnPageReady()
    {
        HideSplash();
        ApplySystemBars(!IsSystemDark());
    }

    /* ───────────────────────── 系统栏 ───────────────────────── */

    private bool IsSystemDark()
    {
        var mode = Resources?.Configuration?.UiMode & Android.Content.Res.UiMode.NightMask;
        return mode == Android.Content.Res.UiMode.NightYes;
    }

    internal void ApplySystemBars(bool dark)
    {
        var window = Window;
        if (window is null) return;
        var background = dark ? DarkBackground : LightBackground;
        _root?.SetBackgroundColor(background);
        _splash?.SetBackgroundColor(background);
        _splashText?.SetTextColor(Color.ParseColor(dark ? "#B2C0B6" : "#4E5D55"));

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            window.SetDecorFitsSystemWindows(false);
            window.SetStatusBarColor(Color.Transparent);
            window.SetNavigationBarColor(Color.Transparent);
            var controller = window.InsetsController;
            if (controller is not null)
            {
                var light = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
                controller.SetSystemBarsAppearance(dark ? 0 : light, light);
            }
        }
        else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            window.SetStatusBarColor(background);
            window.SetNavigationBarColor(background);
            var flags = window.DecorView!.SystemUiFlags;
            if (dark) flags &= ~(SystemUiFlags.LightStatusBar | SystemUiFlags.LightNavigationBar);
            else flags |= SystemUiFlags.LightStatusBar | SystemUiFlags.LightNavigationBar;
            window.DecorView.SystemUiFlags = flags;
        }
    }

    internal void SetKeepScreenOn(bool enabled) => RunOnUiThread(() =>
    {
        var window = Window;
        if (window is null) return;
        if (enabled) window.AddFlags(WindowManagerFlags.KeepScreenOn);
        else window.ClearFlags(WindowManagerFlags.KeepScreenOn);
    });

    /* 系统栏高度实时同步给页面，横竖屏切换、导航方式变化都会跟着更新。
       注意 WindowInsets 给的是物理像素，而页面用的是 CSS 像素，必须按屏幕密度换算，
       否则留白会按 2~3 倍放大。 */
    internal void ApplyPageInsets(int left, int top, int right, int bottom)
    {
        if (left == _insetLeft && top == _insetTop && right == _insetRight && bottom == _insetBottom) return;
        _insetLeft = left; _insetTop = top; _insetRight = right; _insetBottom = bottom;
        var script = $"window.__jmInsets&&window.__jmInsets({ToCss(left)},{ToCss(top)},{ToCss(right)},{ToCss(bottom)})";
        RunOnUiThread(() => _webView?.EvaluateJavascript(script, null));
    }

    private float Density => Resources?.DisplayMetrics?.Density ?? 1f;

    private int ToCss(int physicalPixels) => physicalPixels <= 0 ? 0 : (int)Math.Round(physicalPixels / Density);

    /// <summary>阅读器要沉浸式阅读时把系统栏藏起来，退出时恢复。</summary>
    private void SetSystemBarsVisible(bool visible)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.R) return;
        var controller = Window?.InsetsController;
        if (controller is null) return;
        controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        if (visible) controller.Show(WindowInsets.Type.SystemBars());
        else controller.Hide(WindowInsets.Type.SystemBars());
    }

    /* 返回键交给页面处理：先收浮层 → 退搜索 → 回「发现」，都没有才退出应用。
       用回调拿结果，页面还没加载完时（拿不到 __jmBack）同样会正常退出，不会出现“按了没反应”。 */
    public override void OnBackPressed()
    {
        var web = _webView;
        if (web is null) { base.OnBackPressed(); return; }
        web.EvaluateJavascript("(window.__jmBack&&window.__jmBack())||'exit'", new BackCallback(this));
    }

    private sealed class BackCallback : Java.Lang.Object, Android.Webkit.IValueCallback
    {
        private readonly MainActivity _activity;
        public BackCallback(MainActivity activity) => _activity = activity;

        public void OnReceiveValue(Java.Lang.Object? value)
        {
            var result = value?.ToString()?.Trim('"') ?? "exit";
            if (result == "handled") return;
            _activity.ExitApp();
        }
    }

    /// <summary>页面自己判断已经无路可退时调用。</summary>
    internal void ExitApp() => RunOnUiThread(Finish);

    /* ───────────────────────── 权限 ───────────────────────── */

    private void RequestNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;
        if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;
        RequestPermissions([Android.Manifest.Permission.PostNotifications], NotificationRequestCode);
    }

    internal static bool HasAllFilesAccess()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) return Android.OS.Environment.IsExternalStorageManager;
        return true;
    }

    internal void RequestAllFilesAccess()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                if (Android.OS.Environment.IsExternalStorageManager)
                {
                    Toast.MakeText(this, "已经可以读写手机存储", ToastLength.Short)?.Show();
                    return;
                }
                var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + PackageName));
                StartActivityForResult(intent, StorageRequestCode);
            }
            else
            {
                RequestPermissions([Android.Manifest.Permission.WriteExternalStorage, Android.Manifest.Permission.ReadExternalStorage], StorageRequestCode);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("申请存储权限失败: " + exception.Message);
            Toast.MakeText(this, "无法打开权限页面，请在系统设置中授予「所有文件访问」", ToastLength.Long)?.Show();
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == StorageRequestCode || requestCode == NotificationRequestCode) ReloadPage();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == StorageRequestCode) ReloadPage();
    }

    private void ReloadPage() => RunOnUiThread(() => _webView?.Reload());

    /* ───────────────────────── 原生能力 ───────────────────────── */

    internal string InjectedBootstrap(string html)
    {
        var script = "<script>"
            + $"window.__JMDOWNLOAD_TOKEN__={JsonSerializer.Serialize(BackendHost.Token)};"
            + "window.__JMDOWNLOAD_DESKTOP__=false;"
            + "window.__JMDOWNLOAD_PLATFORM__=\"android\";"
            + $"window.__JMDOWNLOAD_STORAGE__={(HasAllFilesAccess() ? "true" : "false")};"
            + $"window.__JMDOWNLOAD_INSETS__={{left:{ToCss(_insetLeft)},top:{ToCss(_insetTop)},right:{ToCss(_insetRight)},bottom:{ToCss(_insetBottom)}}};"
            + "</script>";
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return head < 0 ? script + html : html.Insert(head + 6, script);
    }

    internal void HandleNativeAction(string url)
    {
        Android.Net.Uri? uri = null;
        try { uri = Android.Net.Uri.Parse(url); } catch { }
        if (uri is null) return;
        switch (uri.Host)
        {
            case "open-folder":
                OpenFolder(uri.GetQueryParameter("path") ?? string.Empty);
                break;
            case "request-storage":
                RequestAllFilesAccess();
                break;
            case "screen":
                SetKeepScreenOn(uri.GetQueryParameter("on") == "1");
                break;
            case "system-bars":
                SetSystemBarsVisible(uri.GetQueryParameter("visible") != "0");
                break;
            case "theme":
                ApplySystemBars(uri.GetQueryParameter("dark") == "1");
                break;
            case "exit":
                ExitApp();
                break;
            case "toast":
                Toast.MakeText(this, uri.GetQueryParameter("text") ?? string.Empty, ToastLength.Short)?.Show();
                break;
            case "haptic":
                _webView?.PerformHapticFeedback(
                    uri.GetQueryParameter("kind") == "strong"
                        ? FeedbackConstants.LongPress
                        : FeedbackConstants.VirtualKey);
                break;
        }
    }

    internal void OpenFolder(string path)
    {
        var directory = string.IsNullOrWhiteSpace(path) ? BackendHost.DefaultDownloadDirectory : path;
        try
        {
            var external = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (!string.IsNullOrEmpty(external) && directory.StartsWith(external, StringComparison.Ordinal))
            {
                var relative = directory[external.Length..].Trim('/', '\\');
                if (!string.IsNullOrEmpty(relative))
                {
                    var document = Android.Provider.DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:" + relative);
                    var view = new Intent(Intent.ActionView);
                    view.SetDataAndType(document, Android.Provider.DocumentsContract.Document.MimeTypeDir);
                    view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                    StartActivity(view);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("打开目录失败: " + exception.Message);
        }
        Toast.MakeText(this, "文件保存在：" + directory, ToastLength.Long)?.Show();
    }

    /// <summary>
    /// 真正的全屏：内容和背景一直铺满整块屏幕（连状态栏、手势条下面也是），
    /// 只把系统栏的高度告诉页面，由 CSS 自己去避开 —— 这样背景能连贯到屏幕边缘，
    /// 而不是在系统栏位置留一条纯色带。
    /// </summary>
    private sealed class RootInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly MainActivity _activity;
        public RootInsetsListener(MainActivity activity) => _activity = activity;

        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (insets is null) return insets!;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars());
                _activity.ApplyPageInsets(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            return insets!;
        }
    }

    private sealed class LocalWebViewClient : WebViewClient
    {
        private readonly MainActivity _activity;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

        public LocalWebViewClient(MainActivity activity) => _activity = activity;

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString() ?? string.Empty;
            if (url.StartsWith("jmd://", StringComparison.Ordinal))
            {
                _activity.HandleNativeAction(url);
                return true;
            }
            return false;
        }

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            var uri = request?.Url;
            if (uri is null || uri.Scheme != "http" || uri.Host != "127.0.0.1") return null;
            var path = uri.Path ?? "/";
            if (path != "/" && path != "/index.html") return null;
            try
            {
                var html = _http.GetStringAsync($"{BackendHost.BaseUrl}/index.html").GetAwaiter().GetResult();
                var bytes = Encoding.UTF8.GetBytes(_activity.InjectedBootstrap(html));
                return new WebResourceResponse("text/html", "utf-8", new MemoryStream(bytes));
            }
            catch (Exception exception)
            {
                AppLog.Error("首页注入失败: " + exception.Message);
                return null;
            }
        }

        public override void OnPageFinished(WebView? view, string? url) => _activity.OnPageReady();
    }
}

/// <summary>跨 Activity 重建记录服务是否已启动，避免重复 startForegroundService。</summary>
internal static class BackendServiceToggle
{
    public static bool Running { get; set; }
}

