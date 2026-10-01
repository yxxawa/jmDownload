using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Net.Http.Headers;
using System.Text.Json;

namespace JmDownload.Mobile;

/// <summary>
/// 前台服务：让本地后端在应用退到后台后继续下载，并在通知栏显示进度。
/// 没有它，Android 会在内存紧张时直接杀掉进程，正在进行的下载就会中断。
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync, Label = "@string/app_name")]
public class BackendService : Service
{
    public const int NotificationId = 710421;
    private const string ChannelId = "jm-download";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private Timer? _timer;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForegroundCompat(BuildNotification(GetString(Resource.String.notification_idle)));
        _ = Task.Run(async () =>
        {
            try
            {
                await BackendHost.StartAsync(this).ConfigureAwait(false);
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BackendHost.Token);
            }
            catch (Exception exception)
            {
                AppLog.Error("后台服务启动后端失败: " + exception);
            }
            RefreshNotification();
        });
        _timer ??= new Timer(_ => RefreshNotification(), null, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12));
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _timer?.Dispose();
        _timer = null;
        BackendHost.Stop();
        base.OnDestroy();
    }

    private void RefreshNotification()
    {
        if (!BackendHost.Ready) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var json = await _http.GetStringAsync($"{BackendHost.BaseUrl}/api/tasks").ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var active = 0;
                if (root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var task in tasks.EnumerateArray())
                    {
                        var status = task.TryGetProperty("status", out var value) ? value.GetString() : null;
                        if (status is "running" or "queued") active++;
                    }
                }
                var text = active > 0 ? GetString(Resource.String.notification_running, active) : GetString(Resource.String.notification_idle);
                var manager = (NotificationManager?)GetSystemService(NotificationService);
                manager?.Notify(NotificationId, BuildNotification(text));
            }
            catch (Exception exception)
            {
                AppLog.Error("刷新通知失败: " + exception.Message);
            }
        });
    }

    private void CreateChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null) return;
        var channel = new NotificationChannel(ChannelId, GetString(Resource.String.notification_channel_name), NotificationImportance.Low)
        {
            Description = GetString(Resource.String.notification_channel_description)
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string text)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.SingleTop);
        var pending = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder
            .SetContentTitle(GetString(Resource.String.notification_title))
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_stat_download)
            .SetContentIntent(pending)
            .SetOngoing(true)
            .SetShowWhen(false);
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) builder.SetPriority((int)NotificationPriority.Low);
        return builder.Build()!;
    }

    private void StartForegroundCompat(Notification notification)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);
    }
}
