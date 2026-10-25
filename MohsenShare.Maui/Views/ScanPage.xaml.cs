
using MohsenShare.Maui.Services.Transfer;
using System.Net;
using System.Text.Json;
using ZXing.Net.Maui;

namespace MohsenShare.Maui.Views;

public partial class ScanPage : ContentPage
{
    private readonly LocalTransferService _transfer;
    private int _handled;

    public ScanPage(LocalTransferService transfer)
    {
        InitializeComponent();

        _transfer = transfer;

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

            try
            {
                var info =
                    JsonSerializer.Deserialize<
                        LocalTransferService.ConnectionInfo>(value);

                if (info == null ||
                    info.App != "MohsenShare" ||
                    !IPAddress.TryParse(info.Ip, out _) ||
                    info.Port is < 1 or > 65535 ||
                    string.IsNullOrWhiteSpace(info.Token))
                {
                    throw new InvalidDataException(
                        "اطلاعات QR معتبر نیست.");
                }

                var file = await FilePicker.Default.PickAsync();

                if (file == null)
                {
                    Interlocked.Exchange(ref _handled, 0);
                    CameraView.IsDetecting = true;
                    return;
                }

                string tempPath = Path.Combine(
                    FileSystem.CacheDirectory,
                    $"{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}");

                try
                {
                    await using (var source = await file.OpenReadAsync())
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

                    await _transfer.SendFileAsync(
                        info.Ip,
                        info.Port,
                        info.Token,
                        tempPath);

                    await DisplayAlertAsync(
                        "ارسال موفق",
                        $"فایل «{file.FileName}» ارسال شد.",
                        "باشه");

                    await Navigation.PopModalAsync();
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(
                    "خطا در ارسال",
                    ex.Message,
                    "باشه");

                Interlocked.Exchange(ref _handled, 0);
                CameraView.IsDetecting = true;
            }
        });
    }

    private async void OnCancelClicked(object sender, EventArgs e)
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
