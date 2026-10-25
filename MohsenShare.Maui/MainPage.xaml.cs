
using MohsenShare.Maui.Services.Transfer;
using MohsenShare.Maui.ViewModels;
using MohsenShare.Maui.Views;
using System.Net;

namespace MohsenShare.Maui;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly LocalTransferService _transfer;

    public MainPage(
        MainViewModel viewModel,
        LocalTransferService transfer)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _transfer = transfer;

        BindingContext = _viewModel;

        // در ویندوز اتصال دستی وجود دارد؛ اسکن QR را به گوشی بسپار.
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
            var result = await _transfer.StartReceiverAsync();

            await Navigation.PushModalAsync(
                new QrPage(
                    _transfer,
                    result.QrPng,
                    result.Address,
                    result.Token));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(
                "خطا در شروع دریافت",
                ex.Message,
                "باشه");
        }
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        await Navigation.PushModalAsync(
            new ScanPage(_transfer));
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

            if (file == null)
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

                await DisplayAlert(
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
