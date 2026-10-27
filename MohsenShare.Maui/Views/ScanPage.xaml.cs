
using System.Net;
using System.Text.Json;
using MohsenShare.Maui.Services.Transfer;
using ZXing.Net.Maui;

namespace MohsenShare.Maui.Views;

public partial class ScanPage : ContentPage
{
    private readonly ITransferSessionService _transferSession;
    private int _handled;

    public ScanPage(ITransferSessionService transferSession)
    {
        InitializeComponent();

        _transferSession = transferSession;

        CameraView.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormat.QrCode,
            AutoRotate = true,
            Multiple = false
        };

        CameraView.IsDetecting = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var permission =
            await Permissions.RequestAsync<Permissions.Camera>();

        if (permission != PermissionStatus.Granted)
        {
            await DisplayAlertAsync(
                "دسترسی دوربین",
                "برای اسکن QR باید مجوز دوربین را فعال کنی.",
                "باشه");

            await Navigation.PopModalAsync();
            return;
        }

        CameraView.IsDetecting = true;
    }

    private void OnBarcodesDetected(
        object sender,
        BarcodeDetectionEventArgs e)
    {
        string? value = e.Results.FirstOrDefault()?.Value;

        if (string.IsNullOrWhiteSpace(value) ||
            Interlocked.Exchange(ref _handled, 1) != 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            CameraView.IsDetecting = false;

            string? tempPath = null;
            bool connected = false;

            try
            {
                QrConnectionPayload? info =
                    JsonSerializer.Deserialize<QrConnectionPayload>(
                        value,
                        TransferProtocol.JsonOptions);

                if (info is null ||
                    !IPAddress.TryParse(info.IpAddress, out IPAddress? ip) ||
                    ip.AddressFamily !=
                        System.Net.Sockets.AddressFamily.InterNetwork ||
                    info.Port is < 1 or > 65535 ||
                    string.IsNullOrWhiteSpace(info.Token) ||
                    info.ProtocolVersion != TransferProtocol.CurrentVersion)
                {
                    throw new InvalidDataException(
                        "اطلاعات QR معتبر نیست یا نسخه پروتکل سازگار نیست.");
                }

                var file = await FilePicker.Default.PickAsync();

                if (file is null)
                {
                    Interlocked.Exchange(ref _handled, 0);
                    CameraView.IsDetecting = true;
                    return;
                }

                string fileName = Path.GetFileName(file.FileName);

                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "file";

                tempPath = Path.Combine(
                    FileSystem.CacheDirectory,
                    $"{Guid.NewGuid():N}_{fileName}");

                await using (Stream source = await file.OpenReadAsync())
                await using (var destination = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    await source.CopyToAsync(destination);
                }

                await DisplayAlertAsync(
                    "در حال اتصال",
                    $"در حال اتصال به {info.DeviceName}...",
                    "باشه");

                await _transferSession.ConnectAsync(
                    info.IpAddress,
                    info.Port,
                    info.Token,
                    localDeviceName:
                        Microsoft.Maui.Devices.DeviceInfo.Current.Name);

                connected = true;

                await _transferSession.QueueFilesAsync(
                    new[] { tempPath });

                await DisplayAlertAsync(
                    "ارسال موفق",
                    $"فایل «{fileName}» ارسال و تأیید شد.",
                    "باشه");

                await _transferSession.DisconnectAsync();
                connected = false;

                await Navigation.PopModalAsync();
            }
            catch (Exception ex)
            {
                if (connected)
                {
                    try
                    {
                        await _transferSession.DisconnectAsync();
                    }
                    catch
                    {
                        // خطای اصلی حفظ می‌شود.
                    }
                }

                await DisplayAlertAsync(
                    "خطا در ارسال",
                    ex.Message,
                    "باشه");

                Interlocked.Exchange(ref _handled, 0);
                CameraView.IsDetecting = true;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempPath) &&
                    File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"حذف فایل موقت ناموفق بود: {ex.Message}");
                    }
                }
            }
        });
    }

    private async void OnCancelClicked(
        object sender,
        EventArgs e)
    {
        CameraView.IsDetecting = false;
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        CameraView.IsDetecting = false;
        base.OnDisappearing();
    }
}
