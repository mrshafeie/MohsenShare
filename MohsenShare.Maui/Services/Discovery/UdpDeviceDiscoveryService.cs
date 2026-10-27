
using MohsenShare.Maui.Models;
using MohsenShare.Maui.Services.Networking;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MohsenShare.Maui.Services.Discovery;

public sealed class UdpDeviceDiscoveryService : IDeviceDiscoveryService
{
    private const int DiscoveryPort = 45678;
    private const string DiscoveryMessage = "MOHSENSHARE_DISCOVERY";
    private static readonly TimeSpan DeviceTimeout =
        TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, Models.DeviceInfo> _devices = new();
    private readonly List<InterfaceWorker> _workers = new();
    private readonly object _lifecycleLock = new();

    private readonly Models.DeviceInfo _localDevice;

    private CancellationTokenSource? _cts;
    private Task? _cleanupTask;

#if ANDROID
    private static global::Android.Net.Wifi.WifiManager.MulticastLock?
        _multicastLock;
#endif

    public event EventHandler<Models.DeviceInfo>? DeviceDiscovered;
    public event EventHandler<string>? DeviceLost;

    public UdpDeviceDiscoveryService()
    {
        _localDevice = new Models.DeviceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = DeviceInfoHelper.GetDeviceName(),
            Platform = DeviceInfoHelper.GetPlatform(),
            Port = DiscoveryPort
        };
    }

    public Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_cts is not null)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

            try
            {
                AcquireAndroidMulticastLock();

                var routes = NetworkRouteResolver.GetBroadcastRoutes();
                var errors = new List<string>();

                foreach (var route in routes)
                {
                    UdpClient? client = null;

                    try
                    {
                        client = new UdpClient(
                            AddressFamily.InterNetwork);

                        client.EnableBroadcast = true;

                        // این سوکت فقط به IP همین رابط متصل است.
                        client.Client.Bind(new IPEndPoint(
                            route.LocalAddress,
                            DiscoveryPort));

                        var worker = new InterfaceWorker(
                            route.LocalAddress,
                            route.BroadcastAddress!,
                            client);

                        _workers.Add(worker);
                        client = null;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(
                            $"{route.InterfaceName} " +
                            $"({route.LocalAddress}): {ex.Message}");
                    }
                    finally
                    {
                        client?.Dispose();
                    }
                }

                if (_workers.Count == 0)
                {
                    string details = errors.Count == 0
                        ? "هیچ رابط شبکه‌ای با IPv4 و Broadcast معتبر پیدا نشد."
                        : string.Join(Environment.NewLine, errors);

                    throw new InvalidOperationException(
                        $"راه‌اندازی کشف دستگاه ناموفق بود.{Environment.NewLine}{details}");
                }

                CancellationToken token = _cts.Token;

                foreach (var worker in _workers)
                {
                    worker.ListenTask = ListenAsync(worker, token);
                    worker.BroadcastTask = BroadcastAsync(worker, token);
                }

                _cleanupTask = CleanupStaleDevicesAsync(token);
            }
            catch
            {
                foreach (var worker in _workers)
                    worker.Client.Dispose();

                _workers.Clear();

                _cts.Dispose();
                _cts = null;

                ReleaseAndroidMulticastLock();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        InterfaceWorker[] workers;
        Task? cleanupTask;

        lock (_lifecycleLock)
        {
            cts = _cts;

            if (cts is null)
                return;

            _cts = null;
            workers = _workers.ToArray();
            _workers.Clear();
            cleanupTask = _cleanupTask;
            _cleanupTask = null;
        }

        try
        {
            await cts.CancelAsync();

            foreach (var worker in workers)
            {
                worker.Client.Dispose();
            }

            var tasks = workers
                .SelectMany(worker => new[]
                {
                    worker.ListenTask,
                    worker.BroadcastTask
                })
                .Where(task => task is not null)
                .Cast<Task>()
                .ToList();

            if (cleanupTask is not null)
                tasks.Add(cleanupTask);

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }
        finally
        {
            cts.Dispose();
            ReleaseAndroidMulticastLock();
        }
    }

    public IReadOnlyCollection<Models.DeviceInfo> GetDevices()
    {
        return _devices.Values
            .OrderBy(device => device.Name)
            .ToArray();
    }

    private async Task BroadcastAsync(
        InterfaceWorker worker,
        CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(
            worker.BroadcastAddress,
            DiscoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = new DiscoveryPacket
                {
                    Message = DiscoveryMessage,
                    Device = _localDevice
                };

                byte[] data = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(packet));

                await worker.Client.SendAsync(
                    data,
                    endpoint);

                await Task.Delay(
                    TimeSpan.FromSeconds(3),
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MohsenShare] UDP send " +
                    $"{worker.LocalAddress} -> " +
                    $"{worker.BroadcastAddress}: {ex.Message}");

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ListenAsync(
        InterfaceWorker worker,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result =
                    await worker.Client.ReceiveAsync(
                        cancellationToken);

                string json = Encoding.UTF8.GetString(
                    result.Buffer);

                DiscoveryPacket? packet;

                try
                {
                    packet = JsonSerializer.Deserialize<DiscoveryPacket>(
                        json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (packet?.Device is null ||
                    packet.Message != DiscoveryMessage ||
                    packet.Device.Id == _localDevice.Id)
                {
                    continue;
                }

                Models.DeviceInfo device = packet.Device;

                // آدرس مبدأ واقعی بسته قابل‌اعتمادتر از
                // آدرسی است که داخل JSON اعلام شده است.
                device.IpAddress =
                    result.RemoteEndPoint.Address.ToString();

                device.LastSeen = DateTime.UtcNow;

                bool isNew = !_devices.TryGetValue(
                    device.Id,
                    out Models.DeviceInfo? previous);

                _devices[device.Id] = device;

                if (isNew ||
                    previous is null ||
                    previous.IpAddress != device.IpAddress ||
                    previous.Name != device.Name ||
                    previous.Platform != device.Platform)
                {
                    DeviceDiscovered?.Invoke(this, device);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MohsenShare] UDP listen " +
                    $"{worker.LocalAddress}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MohsenShare] Invalid discovery packet: {ex.Message}");
            }
        }
    }

    private async Task CleanupStaleDevicesAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    cancellationToken);

                DateTime cutoff = DateTime.UtcNow - DeviceTimeout;

                foreach (var entry in _devices)
                {
                    if (entry.Value.LastSeen >= cutoff)
                        continue;

                    if (_devices.TryRemove(entry.Key, out _))
                    {
                        DeviceLost?.Invoke(this, entry.Key);
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

#if ANDROID
    private static void AcquireAndroidMulticastLock()
    {
        try
        {
            var context = global::Android.App.Application.Context;

            if (context.GetSystemService(
                    global::Android.Content.Context.WifiService)
                is global::Android.Net.Wifi.WifiManager wifiManager)
            {
                if (_multicastLock is null)
                {
                    _multicastLock =
                        wifiManager.CreateMulticastLock(
                            "MohsenShareMulticastLock");
                }

                if (!_multicastLock!.IsHeld)
                    _multicastLock.Acquire();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MohsenShare] Multicast lock: {ex.Message}");
        }
    }

    private static void ReleaseAndroidMulticastLock()
    {
        try
        {
            if (_multicastLock is not null &&
                _multicastLock.IsHeld)
            {
                _multicastLock.Release();
            }

            _multicastLock = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MohsenShare] Release multicast lock: {ex.Message}");
        }
    }
#else
    private static void AcquireAndroidMulticastLock()
    {
    }

    private static void ReleaseAndroidMulticastLock()
    {
    }
#endif

    private sealed class InterfaceWorker
    {
        public InterfaceWorker(
            IPAddress localAddress,
            IPAddress broadcastAddress,
            UdpClient client)
        {
            LocalAddress = localAddress;
            BroadcastAddress = broadcastAddress;
            Client = client;
        }

        public IPAddress LocalAddress { get; }
        public IPAddress BroadcastAddress { get; }
        public UdpClient Client { get; }

        public Task? ListenTask { get; set; }
        public Task? BroadcastTask { get; set; }
    }

    private sealed class DiscoveryPacket
    {
        public string Message { get; set; } = string.Empty;
        public Models.DeviceInfo? Device { get; set; }
    }
}
