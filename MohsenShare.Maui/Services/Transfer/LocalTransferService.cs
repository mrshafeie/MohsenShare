
using QRCoder;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MohsenShare.Maui.Services.Transfer;

public sealed class LocalTransferService
{
    public const int TransferPort = 45679;

    private const long MaxFileSize = 10L * 1024 * 1024 * 1024;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private string? _token;

    public event EventHandler<string>? FileReceived;

    public sealed record ConnectionInfo(
        string App,
        string Ip,
        int Port,
        string Token);

    private sealed record TransferHeader(
        string Token,
        string FileName,
        long Length);

    public async Task<(byte[] QrPng, string Address, string Token)>
        StartReceiverAsync()
    {
        await StopAsync();

        IPAddress ip = GetLocalIPv4();

        _token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));

        _listener = new TcpListener(IPAddress.Any, TransferPort);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _receiveTask = ReceiveLoopAsync(_cts.Token);

        var info = new ConnectionInfo(
            "MohsenShare",
            ip.ToString(),
            TransferPort,
            _token);

        string json = JsonSerializer.Serialize(info);

        using var generator = new QRCodeGenerator();

        using var data = generator.CreateQrCode(
            json,
            QRCodeGenerator.ECCLevel.Q);

        using var qr = new PngByteQRCode(data);

        byte[] png = qr.GetGraphic(10);

        return (png, $"{ip}:{TransferPort}", _token);
    }

    public async Task SendFileAsync(
        string ip,
        int port,
        string token,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ip, out _))
            throw new ArgumentException("آدرس IP معتبر نیست.", nameof(ip));

        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("فایل انتخاب‌شده پیدا نشد.", filePath);

        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);

        if (file.Length > MaxFileSize)
            throw new InvalidOperationException("حجم فایل بیشتر از حد مجاز است.");

        using var client = new TcpClient();

        await client.ConnectAsync(ip, port, cancellationToken);

        await using NetworkStream stream = client.GetStream();

        var header = new TransferHeader(
            token,
            Path.GetFileName(filePath),
            file.Length);

        byte[] headerBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(header) + "\n");

        await stream.WriteAsync(headerBytes, cancellationToken);

        await file.CopyToAsync(stream, 81920, cancellationToken);

        await stream.FlushAsync(cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;

        if (listener == null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);

                await using NetworkStream stream = client.GetStream();

                string? line = await ReadLineAsync(stream, cancellationToken);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var header = JsonSerializer.Deserialize<TransferHeader>(line);

                if (header == null ||
                    string.IsNullOrWhiteSpace(header.FileName) ||
                    header.Length < 0 ||
                    header.Length > MaxFileSize ||
                    !IsValidToken(header.Token))
                {
                    continue;
                }

                string folder = Path.Combine(
                    FileSystem.Current.AppDataDirectory,
                    "ReceivedFiles");

                Directory.CreateDirectory(folder);

                string safeName = Path.GetFileName(header.FileName);

                if (string.IsNullOrWhiteSpace(safeName))
                    continue;

                string destination = Path.Combine(
                    folder,
                    $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeName}");

                await using (var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    long remaining = header.Length;

                    while (remaining > 0)
                    {
                        int count = (int)Math.Min(buffer.Length, remaining);

                        int read = await stream.ReadAsync(
                            buffer.AsMemory(0, count),
                            cancellationToken);

                        if (read == 0)
                            throw new EndOfStreamException(
                                "ارتباط قبل از پایان انتقال قطع شد.");

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);

                        remaining -= read;
                    }
                }

                FileReceived?.Invoke(this, destination);
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MohsenShare] Receive error: {ex}");
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    private bool IsValidToken(string suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
            return false;

        try
        {
            byte[] expected = Convert.FromHexString(_token);
            byte[] supplied = Convert.FromHexString(suppliedToken);

            return expected.Length == supplied.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       expected,
                       supplied);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ReadLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] oneByte = new byte[1];

        while (buffer.Length < 8192)
        {
            int read = await stream.ReadAsync(
                oneByte.AsMemory(),
                cancellationToken);

            if (read == 0)
                return null;

            if (oneByte[0] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer.ToArray());

            buffer.WriteByte(oneByte[0]);
        }

        throw new InvalidDataException("سربرگ انتقال بیش از حد بزرگ است.");
    }

    private static IPAddress GetLocalIPv4()
    {
        var interfaces = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderBy(n => n.NetworkInterfaceType ==
                          NetworkInterfaceType.Wireless80211 ? 0 : 1);

        var addresses = interfaces
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a =>
                a.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(a) &&
                !a.ToString().StartsWith("169.254."))
            .ToList();

        var result = addresses.FirstOrDefault(a =>
            a.ToString().StartsWith("192.168.") ||
            a.ToString().StartsWith("10.") ||
            IsPrivate172(a));

        return result ?? throw new InvalidOperationException(
            "IP شبکه پیدا نشد. دستگاه را به Wi-Fi وصل کن.");
    }

    private static bool IsPrivate172(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        return bytes[0] == 172 &&
               bytes[1] >= 16 &&
               bytes[1] <= 31;
    }

    public async Task StopAsync()
    {
        var cts = _cts;

        if (cts == null)
            return;

        _cts = null;

        await cts.CancelAsync();

        _listener?.Stop();
        _listener = null;

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        _receiveTask = null;
        cts.Dispose();
        _token = null;
    }
}
