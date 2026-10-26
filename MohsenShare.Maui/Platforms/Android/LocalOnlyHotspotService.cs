
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace MohsenShare.Maui.Platforms.Android;

public sealed class LocalOnlyHotspotService : IDisposable
{
    private readonly WifiManager _wifiManager;

    private WifiManager.LocalOnlyHotspotReservation? _reservation;
    private HotspotCallback? _callback;
    private HotspotInfo? _currentInfo;
    private TaskCompletionSource<HotspotInfo>? _pendingStart;
    private bool _disposed;

    public LocalOnlyHotspotService()
    {
        var context = global::Android.App.Application.Context;

        _wifiManager = (WifiManager?)context.GetSystemService(
            Context.WifiService)
            ?? throw new InvalidOperationException(
                "سرویس Wi-Fi اندروید در دسترس نیست.");
    }

    public sealed record HotspotInfo(string Ssid, string Password);

    public async Task<HotspotInfo> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_reservation is not null && _currentInfo is not null)
            return _currentInfo;

        await RequestPermissionAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<HotspotInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingStart = completion;

        using var registration = cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);
        });

        var callback = new HotspotCallback(
            OnHotspotStarted,
            OnHotspotFailed,
            OnHotspotStopped);

        _callback = callback;

        try
        {
            _wifiManager.StartLocalOnlyHotspot(
                callback,
                new Handler(Looper.MainLooper));
        }
        catch (Exception ex)
        {
            _pendingStart = null;
            _callback = null;

            throw new InvalidOperationException(
                "شروع شبکه Wi-Fi محلی ناموفق بود.", ex);
        }

        try
        {
            return await completion.Task;
        }
        finally
        {
            if (ReferenceEquals(_pendingStart, completion))
                _pendingStart = null;
        }
    }

    private void OnHotspotStarted(
        WifiManager.LocalOnlyHotspotReservation reservation)
    {
        if (_disposed)
        {
            reservation.Close();
            return;
        }

        try
        {
            string ssid;
            string password;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var config = reservation.SoftApConfiguration;

                ssid = config?.Ssid ?? string.Empty;
                password = config?.Passphrase ?? string.Empty;
            }
            else
            {
#pragma warning disable CS0618
                var config = reservation.WifiConfiguration;

                ssid = config?.Ssid?.Trim('"') ?? string.Empty;
                password = config?.PreSharedKey?.Trim('"') ?? string.Empty;
#pragma warning restore CS0618
            }

            if (string.IsNullOrWhiteSpace(ssid) ||
                string.IsNullOrWhiteSpace(password))
            {
                reservation.Close();

                _pendingStart?.TrySetException(
                    new InvalidOperationException(
                        "اندروید نام شبکه یا رمز آن را برنگرداند."));

                return;
            }

            _reservation = reservation;
            _currentInfo = new HotspotInfo(ssid, password);

            _pendingStart?.TrySetResult(_currentInfo);
        }
        catch (Exception ex)
        {
            reservation.Close();

            _pendingStart?.TrySetException(
                new InvalidOperationException(
                    "خواندن اطلاعات شبکه ناموفق بود.", ex));
        }
    }

    private void OnHotspotFailed(
        LocalOnlyHotspotCallbackErrorCode reason)
    {
        _pendingStart?.TrySetException(
            new InvalidOperationException(
                $"اندروید نتوانست شبکه محلی ایجاد کند. علت: {reason}"));

        _callback = null;
    }

    private void OnHotspotStopped()
    {
        _reservation = null;
        _currentInfo = null;

        _pendingStart?.TrySetException(
            new InvalidOperationException(
                "شبکه Wi-Fi محلی متوقف شد."));

        _callback = null;
    }

    private static async Task RequestPermissionAsync()
    {
        var status = await Permissions.RequestAsync<HotspotPermission>();

        if (status != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException(
                "مجوز Nearby Wi-Fi Devices یا Location داده نشده است.");
        }
    }

    private sealed class HotspotPermission
        : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[]
            RequiredPermissions =>
            Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                ? new[]
                {
                    ("android.permission.NEARBY_WIFI_DEVICES", true)
                }
                : new[]
                {
                    (
                        global::Android.Manifest.Permission.AccessFineLocation,
                        true
                    )
                };
    }

    private sealed class HotspotCallback
        : WifiManager.LocalOnlyHotspotCallback
    {
        private readonly Action<WifiManager.LocalOnlyHotspotReservation>
            _started;
        private readonly Action<LocalOnlyHotspotCallbackErrorCode> _failed;
        private readonly Action _stopped;

        public HotspotCallback(
            Action<WifiManager.LocalOnlyHotspotReservation> started,
            Action<LocalOnlyHotspotCallbackErrorCode> failed,
            Action stopped)
        {
            _started = started;
            _failed = failed;
            _stopped = stopped;
        }

        public override void OnStarted(
            WifiManager.LocalOnlyHotspotReservation reservation)
        {
            _started(reservation);
        }

        public override void OnFailed(
            LocalOnlyHotspotCallbackErrorCode reason)
        {
            _failed(reason);
        }

        public override void OnStopped()
        {
            _stopped();
        }
    }

    public void Stop()
    {
        _pendingStart?.TrySetCanceled();

        var reservation = _reservation;
        _reservation = null;
        _currentInfo = null;
        _callback = null;

        reservation?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
