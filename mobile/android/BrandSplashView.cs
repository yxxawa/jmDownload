using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using Path = Android.Graphics.Path;

namespace JmDownload.Mobile;

/// <summary>Draws the bundled brand artwork while the local service and first UI frame start.</summary>
internal sealed class BrandSplashView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    private readonly Bitmap? _mascot;
    private readonly Bitmap? _avatar;
    private readonly float _density;
    private readonly bool _animate = Build.VERSION.SdkInt < BuildVersionCodes.O || ValueAnimator.AreAnimatorsEnabled();
    private readonly long _started = SystemClock.UptimeMillis();
    private bool _dark;
    private bool _failed;
    private string _status = "正在打开你的漫画世界";

    internal BrandSplashView(Context context) : base(context)
    {
        _density = context.Resources!.DisplayMetrics!.Density;
        _mascot = BitmapFactory.DecodeResource(context.Resources, Resource.Drawable.brand_welcome);
        _avatar = BitmapFactory.DecodeResource(context.Resources, Resource.Drawable.brand_avatar);
        ContentDescription = "JM Download，橙奈正在为你打开漫画世界";
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        SetLayerType(LayerType.Hardware, null);
    }

    internal void SetDark(bool dark)
    {
        _dark = dark;
        Invalidate();
    }

    internal void SetStatus(string status, bool failed = false)
    {
        _status = status;
        _failed = failed;
        Clickable = failed;
        Focusable = failed;
        ContentDescription = "JM Download，" + status;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var w = Width / _density;
        var h = Height / _density;
        var compact = h < 660;
        var landscape = w > h;
        var background = _dark ? Color.ParseColor("#17191D") : Color.ParseColor("#FBFBFD");
        var ink = _dark ? Color.ParseColor("#F6F5F2") : Color.ParseColor("#252732");
        var muted = _dark ? Color.ParseColor("#A5A6AE") : Color.ParseColor("#8C8D96");
        var orange = Color.ParseColor("#F7A51A");
        canvas.DrawColor(background);
        canvas.Save();
        canvas.Scale(_density, _density);
        var top = Math.Max(48f, h * .085f);
        var contentWidth = Math.Min(w - 48, 420);
        var left = (w - contentWidth) / 2;

        if (landscape)
        {
            DrawArtwork(canvas, new RectF(w * .45f, 32, w * .93f, h - 24), orange);
            DrawHeader(canvas, 32, 48, ink, muted);
            DrawText(canvas, "好故事，正要开始。", 32, h * .55f, 25, ink, true);
            DrawText(canvas, "橙奈陪你，翻开下一页", 32, h * .55f + 30, 12, muted);
            DrawLoader(canvas, 32, h - 68, Math.Min(180, w * .30f), orange, muted);
        }
        else
        {
            DrawHeader(canvas, left, top, ink, muted);
            var artTop = top + (compact ? 55 : 65);
            var footerTop = h - (compact ? 150 : 174);
            var artBottom = footerTop - 20;
            var artWidth = Math.Min(contentWidth + 18, 380);
            DrawArtwork(canvas, new RectF((w - artWidth) / 2, artTop, (w + artWidth) / 2, artBottom), orange);
            DrawText(canvas, "好故事，正要开始。", w / 2, footerTop + 16, compact ? 24 : 27, ink, true, Paint.Align.Center);
            DrawText(canvas, "橙奈陪你，翻开下一页", w / 2, footerTop + 44, 12, muted, false, Paint.Align.Center);
            DrawLoader(canvas, (w - 144) / 2, footerTop + 70, 144, orange, muted);
            DrawText(canvas, "JM DOWNLOAD  ·  COMIC & READING", w / 2, h - 30, 8, muted, false, Paint.Align.Center);
        }
        canvas.Restore();
        if (_animate && !_failed && IsAttachedToWindow && Visibility == ViewStates.Visible) PostInvalidateOnAnimation();
    }

    private void DrawHeader(Canvas canvas, float left, float top, Color ink, Color muted)
    {
        if (_avatar is not null)
        {
            canvas.Save();
            using var clip = new Path();
            clip.AddRoundRect(new RectF(left, top, left + 42, top + 42), 12, 12, Path.Direction.Cw!);
            canvas.ClipPath(clip);
            canvas.DrawBitmap(_avatar, null, new RectF(left, top, left + 42, top + 42), _paint);
            canvas.Restore();
        }
        DrawText(canvas, "JM Download", left + 54, top + 19, 18, ink, true);
        DrawText(canvas, "你的随身漫画书架", left + 54, top + 37, 10, muted);
    }

    private void DrawArtwork(Canvas canvas, RectF available, Color orange)
    {
        var cx = available.CenterX();
        var cy = available.Top + available.Height() * .48f;
        var radius = Math.Min(available.Width() * .49f, available.Height() * .43f);
        using (var glow = new RadialGradient(cx, cy, Math.Max(1, radius),
            Color.ParseColor(_dark ? "#3D2D20" : "#FFF0D5"), Color.ParseColor(_dark ? "#1F1E20" : "#FBFBFD"), Shader.TileMode.Clamp!))
        {
            _paint.SetShader(glow);
            canvas.DrawCircle(cx, cy, radius, _paint);
            _paint.SetShader(null);
        }
        _paint.Color = Color.ParseColor(_dark ? "#7A5B30" : "#F4D6A4");
        _paint.SetStyle(Paint.Style.Stroke!);
        _paint.StrokeWidth = 1;
        canvas.DrawCircle(cx, cy, radius * .86f, _paint);
        _paint.SetStyle(Paint.Style.Fill!);
        _paint.Color = orange;
        canvas.DrawCircle(cx + radius * .80f, cy - radius * .55f, 3, _paint);
        canvas.DrawCircle(cx - radius * .90f, cy + radius * .15f, 2, _paint);
        if (_mascot is null) return;
        var scale = Math.Min(available.Width() / _mascot.Width, available.Height() / _mascot.Height);
        var width = _mascot.Width * scale;
        var height = _mascot.Height * scale;
        var target = new RectF(cx - width / 2, available.Bottom - height, cx + width / 2, available.Bottom);
        _paint.Alpha = 255;
        canvas.DrawBitmap(_mascot, null, target, _paint);
    }

    private void DrawLoader(Canvas canvas, float left, float top, float width, Color orange, Color muted)
    {
        _paint.Color = Color.ParseColor(_dark ? "#353235" : "#ECECF0");
        canvas.DrawRoundRect(new RectF(left, top, left + width, top + 3), 2, 2, _paint);
        var seconds = (SystemClock.UptimeMillis() - _started) / 1000f;
        var phase = _animate ? (float)((Math.Sin(seconds * 2.4 - Math.PI / 2) + 1) / 2) : .45f;
        var segment = width * .28f;
        _paint.Color = _failed ? Color.ParseColor("#CE625E") : orange;
        canvas.DrawRoundRect(new RectF(left + phase * (width - segment), top, left + phase * (width - segment) + segment, top + 3), 2, 2, _paint);
        var text = _status;
        DrawText(canvas, text, left + width / 2, top + 25, 10, muted, false, Paint.Align.Center);
    }

    private void DrawText(Canvas canvas, string text, float x, float y, float size, Color color, bool bold = false, Paint.Align? align = null)
    {
        _paint.Color = color;
        _paint.TextSize = size;
        _paint.TextAlign = align ?? Paint.Align.Left!;
        _paint.SetTypeface(bold ? Typeface.DefaultBold : Typeface.Default);
        _paint.Alpha = 255;
        canvas.DrawText(text, x, y, _paint);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mascot?.Recycle();
            _mascot?.Dispose();
            _avatar?.Recycle();
            _avatar?.Dispose();
            _paint.Dispose();
        }
        base.Dispose(disposing);
    }
}
