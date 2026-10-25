using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MohsenShare.Maui.Services.Discovery;

public sealed class UdpDeviceDiscoveryService : IDeviceDiscoveryService
{
    private const int DiscoveryPort = 45678;
    private const string DiscoveryMessage = "MOHSENSHARE_DISCOVERY";

    private readonly Dictionary<string, Models.DeviceInfo> _devices = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _broadcastTask;

    private readonly Models.DeviceInfo _localDevice;

#if ANDROID
    private static global::Android.Net.Wifi.WifiManager.MulticastLock? _multicastLock;
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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
            return Task.CompletedTask;

        // فعال‌سازی Multicast Lock در اندروید برای جلوگیری از فیلتر شدن بسته‌های UDP
#if ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            if (context.GetSystemService(global::Android.Content.Context.WifiService) is global::Android.Net.Wifi.WifiManager wifiManager)
            {
                _multicastLock = wifiManager.CreateMulticastLock("MohsenShareMulticastLock");
                _multicastLock.Acquire();
            }
        }
        catch
        {
            // مدیریت خطا در صورت عدم دسترسی
        }
#endif

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        _listenTask = ListenAsync(_cts.Token);
        _broadcastTask = BroadcastAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts == null)
            return;

        await _cts.CancelAsync();

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

#if ANDROID
        try
        {
            if (_multicastLock != null && _multicastLock.IsHeld)
            {
                _multicastLock.Release();
                _multicastLock = null;
            }
        }
        catch { }
#endif

        if (_listenTask != null)
        {
            try { await _listenTask; } catch { }
        }

        if (_broadcastTask != null)
        {
            try { await _broadcastTask; } catch { }
        }

        _listenTask = null;
        _broadcastTask = null;

        _cts.Dispose();
        _cts = null;
    }

    public IReadOnlyCollection<Models.DeviceInfo> GetDevices()
    {
        return _devices.Values.ToList().AsReadOnly();
    }

    private async Task BroadcastAsync(CancellationToken cancellationToken)
    {
        if (_udpClient == null)
            return;

        var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var message = new DiscoveryPacket
                {
                    Message = DiscoveryMessage,
                    Device = _localDevice
                };

                string json = JsonSerializer.Serialize(message);
                byte[] data = Encoding.UTF8.GetBytes(json);

                // اصلاح این خط (حذف cancellationToken از پارامترهای SendAsync یا استفاده از IP به صورت رشته)
                await _udpClient.SendAsync(data, data.Length, endpoint);

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_udpClient == null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                string json = Encoding.UTF8.GetString(result.Buffer);

                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json);

                if (packet == null || packet.Message != DiscoveryMessage || packet.Device == null)
                    continue;

                if (packet.Device.Id == _localDevice.Id)
                    continue;

                packet.Device.IpAddress = result.RemoteEndPoint.Address.ToString();
                packet.Device.LastSeen = DateTime.UtcNow;

                bool isNew = !_devices.ContainsKey(packet.Device.Id);
                _devices[packet.Device.Id] = packet.Device;

                if (isNew)
                {
                    DeviceDiscovered?.Invoke(this, packet.Device);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // نادیده گرفتن پاکت‌های نامعتبر
            }
        }
    }

    private sealed class DiscoveryPacket
    {
        public string Message { get; set; } = string.Empty;
        public Models.DeviceInfo? Device { get; set; }
    }
}