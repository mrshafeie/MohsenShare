
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi.P2p;
using Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace MohsenShare.Maui.Platforms.Android;

public sealed class WifiDirectService
{
    private readonly WifiP2pManager _manager;
    private readonly WifiP2pManager.Channel _channel;
    private readonly Context _context;

    public WifiDirectService()
    {
        _context = global::Android.App.Application.Context;

        _manager = (WifiP2pManager?)_context.GetSystemService(
            Context.WifiP2pService)
            ?? throw new InvalidOperationException(
                "Wi-Fi Direct روی این دستگاه در دسترس نیست.");

        _channel = _manager.Initialize(
            _context,
            Looper.MainLooper,
            null)
            ?? throw new InvalidOperationException(
                "مقداردهی اولیه Wi-Fi Direct ناموفق بود.");
    }

    public async Task RequestPermissionAsync()
    {
        var status = await Permissions.RequestAsync<WifiDirectPermission>();

        if (status != PermissionStatus.Granted)
        {
            throw new UnauthorizedAccessException(
                "مجوز دستگاه‌های نزدیک برای Wi-Fi Direct داده نشده است.");
        }
    }

    public async Task DiscoverPeersAsync(
        CancellationToken cancellationToken = default)
    {
        await RequestPermissionAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        _manager.DiscoverPeers(
            _channel,
            new ActionListener(
                () => completion.TrySetResult(),
                reason => completion.TrySetException(
                    new InvalidOperationException(
                        $"جست‌وجوی دستگاه‌ها ناموفق بود: {reason}"))));

        await completion.Task;
    }

    public async Task<IReadOnlyList<WifiDirectPeer>> GetPeersAsync(
        CancellationToken cancellationToken = default)
    {
        await RequestPermissionAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var completion =
            new TaskCompletionSource<IReadOnlyList<WifiDirectPeer>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        _manager.RequestPeers(
            _channel,
            new PeerListListener(devices =>
            {
                var peers = devices
                    .Select(device => new WifiDirectPeer(
                        device.DeviceName ?? "دستگاه ناشناس",
                        device.DeviceAddress ?? string.Empty,
                        device.Status.ToString()))
                    .ToArray();

                completion.TrySetResult(peers);
            }));

        return await completion.Task;
    }

    public async Task ConnectAsync(
        string deviceAddress,
        CancellationToken cancellationToken = default)
    {
        await RequestPermissionAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(deviceAddress))
        {
            throw new ArgumentException(
                "آدرس دستگاه معتبر نیست.",
                nameof(deviceAddress));
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        var config = new WifiP2pConfig
        {
            DeviceAddress = deviceAddress
        };

        _manager.Connect(
            _channel,
            config,
            new ActionListener(
                () => completion.TrySetResult(),
                reason => completion.TrySetException(
                    new InvalidOperationException(
                        $"درخواست اتصال ناموفق بود: {reason}"))));

        await completion.Task;
    }

    public async Task<WifiDirectConnectionInfo> GetConnectionInfoAsync(
        CancellationToken cancellationToken = default)
    {
        await RequestPermissionAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var completion =
            new TaskCompletionSource<WifiDirectConnectionInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));

        _manager.RequestConnectionInfo(
            _channel,
            new ConnectionInfoListener(info =>
            {
                var hostAddress = info?.GroupOwnerAddress?.HostAddress;

                if (string.IsNullOrWhiteSpace(hostAddress))
                {
                    completion.TrySetException(
                        new InvalidOperationException(
                            "اطلاعات اتصال در دسترس نیست؛ ابتدا اتصال را برقرار کنید."));
                    return;
                }

                completion.TrySetResult(
                    new WifiDirectConnectionInfo(
                        info!.IsGroupOwner,
                        hostAddress));
            }));

        return await completion.Task;
    }

    public sealed record WifiDirectPeer(
        string Name,
        string Address,
        string Status);

    public sealed record WifiDirectConnectionInfo(
        bool IsGroupOwner,
        string GroupOwnerAddress);

    private sealed class WifiDirectPermission
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

    private sealed class ActionListener
        : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        private readonly Action _success;
        private readonly Action<WifiP2pFailureReason> _failure;

        public ActionListener(
            Action success,
            Action<WifiP2pFailureReason> failure)
        {
            _success = success;
            _failure = failure;
        }

        public void OnSuccess() => _success();

        public void OnFailure(WifiP2pFailureReason reason)
            => _failure(reason);
    }

    private sealed class PeerListListener
        : Java.Lang.Object, WifiP2pManager.IPeerListListener
    {
        private readonly Action<IReadOnlyList<WifiP2pDevice>> _callback;

        public PeerListListener(
            Action<IReadOnlyList<WifiP2pDevice>> callback)
        {
            _callback = callback;
        }

        public void OnPeersAvailable(WifiP2pDeviceList? peers)
        {
            _callback(
                peers?.DeviceList.ToArray()
                ?? Array.Empty<WifiP2pDevice>());
        }
    }

    private sealed class ConnectionInfoListener
        : Java.Lang.Object, WifiP2pManager.IConnectionInfoListener
    {
        private readonly Action<WifiP2pInfo?> _callback;

        public ConnectionInfoListener(
            Action<WifiP2pInfo?> callback)
        {
            _callback = callback;
        }

        public void OnConnectionInfoAvailable(WifiP2pInfo? info)
            => _callback(info);
    }
}
