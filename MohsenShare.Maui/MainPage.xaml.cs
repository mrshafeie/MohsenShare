
using MohsenShare.Maui.Services.Transfer;
using MohsenShare.Maui.ViewModels;
using MohsenShare.Maui.Views;
using System.Net;

#if ANDROID
using MohsenShare.Maui.Platforms.Android;
#endif

namespace MohsenShare.Maui;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly LocalTransferService _transfer;

#if ANDROID
    private readonly LocalOnlyHotspotService _hotspot;
#endif

    public MainPage(
        MainViewModel viewModel,
        LocalTransferService transfer
#if ANDROID
        , LocalOnlyHotspotService hotspot
#endif
        )
    {
        InitializeComponent();

        _viewModel = viewModel;
        _transfer = transfer;

#if ANDROID
        _hotspot = hotspot;
#endif

        BindingContext = _viewModel;

        ScanQrButton.IsVisible =
            DeviceInfo.Platform != DevicePlatform.WinUI;
    }

    private async void OnScanClicked(object sender, EventArgs e)
    {
        await _viewModel.ToggleScanningAsync();
    }

    private async void OnReceiveClicked(object sender, EventArgs e)
    {
        try
        {
#if ANDROID
            var hotspotInfo = await _hotspot.StartAsync();

            await DisplayAlertAsync(
                "شبکه محلی آماده است",
                $"در ویندوز به شبکه زیر وصل شو:\n\n" +
                $"نام شبکه: {hotspotInfo.Ssid}\n" +
                $"رمز شبکه: {hotspotInfo.Password}\n\n" +
                "این شبکه برای ارتباط مستقیم محلی است و به اینترنت نیاز ندارد.",
                "ادامه");
#endif

            var result = await _transfer.StartReceiverAsync();

            Action? stopNetwork = null;

#if ANDROID
            stopNetwork = _hotspot.Stop;
#endif

            await Navigation.PushModalAsync(
                new QrPage(
                    _transfer,
                    result.QrPng,
                    result.Address,
                    result.Token,
                    stopNetwork));
        }
        catch (Exception ex)
        {
#if ANDROID
            _hotspot.Stop();
#endif

            await DisplayAlertAsync(
                "خطا در شروع دریافت",
                ex.Message,
                "باشه");
        }
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new ScanPage(_transfer));
    }

    private async void OnManualSendClicked(
        object sender,
        EventArgs e)
    {
        try
        {
            string? ip = await DisplayPromptAsync(
                "اتصال دستی",
                "IP دستگاه دریافت‌کننده:");

            if (string.IsNullOrWhiteSpace(ip))
                return;

            if (!IPAddress.TryParse(ip.Trim(), out _))
            {
                await DisplayAlertAsync(
                    "خطا",
                    "آدرس IP معتبر نیست.",
                    "باشه");
                return;
            }

            string? portText = await DisplayPromptAsync(
                "اتصال دستی",
                "پورت دستگاه مقصد:",
                initialValue: LocalTransferService.TransferPort.ToString(),
                keyboard: Keyboard.Numeric);

            if (!int.TryParse(portText, out int port) ||
                port is < 1 or > 65535)
            {
                await DisplayAlertAsync(
                    "خطا",
                    "پورت معتبر نیست.",
                    "باشه");
                return;
            }

            string? token = await DisplayPromptAsync(
                "اتصال دستی",
                "کد اتصال نمایش‌داده‌شده روی دستگاه مقصد:");

            if (string.IsNullOrWhiteSpace(token))
                return;

            var file = await FilePicker.Default.PickAsync();

            if (file is null)
                return;

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
                    ip.Trim(),
                    port,
                    token.Trim(),
                    tempPath);

                await DisplayAlertAsync(
                    "ارسال موفق",
                    $"فایل «{file.FileName}» ارسال شد.",
                    "باشه");
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
        }
    }

    protected override async void OnDisappearing()
    {
        await _viewModel.StopScanningAsync();
        base.OnDisappearing();
    }
}
