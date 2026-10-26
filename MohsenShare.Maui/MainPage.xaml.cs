using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Transfer;
using MohsenShare.Maui.ViewModels;
using MohsenShare.Maui.Views;

#if ANDROID
using MohsenShare.Maui.Platforms.Android;
#endif

namespace MohsenShare.Maui;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly LocalTransferService _transfer;
    private readonly IDeviceDiscoveryService _discovery;

#if ANDROID
    private readonly LocalOnlyHotspotService _hotspot;
#endif

    public MainPage(
        MainViewModel viewModel,
        LocalTransferService transfer,
        IDeviceDiscoveryService discovery
#if ANDROID
        , LocalOnlyHotspotService hotspot
#endif
        )
    {
        InitializeComponent();

        _viewModel = viewModel;
        _transfer = transfer;
        _discovery = discovery;

#if ANDROID
        _hotspot = hotspot;
#endif

        BindingContext = _viewModel;
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        try
        {
#if ANDROID
            await StartAndroidSendAsync();
#else
            await StartWindowsSendAsync();
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(
                "خطا در ارسال",
                ex.Message,
                "باشه");
        }
    }

#if ANDROID
    private async Task StartAndroidSendAsync()
    {
        var hotspotInfo = await _hotspot.StartAsync();

        await DisplayAlertAsync(
            "شبکه آماده است",
            $"دستگاه دریافت‌کننده باید به این شبکه متصل شود:\n\n" +
            $"نام شبکه: {hotspotInfo.Ssid}\n" +
            $"رمز شبکه: {hotspotInfo.Password}\n\n" +
            "بعد از اتصال، QR دستگاه دریافت‌کننده را اسکن کن.",
            "باشه");

        await Navigation.PushModalAsync(
            new ScanPage(_transfer));
    }
#endif
    private async Task StartWindowsSendAsync()
    {
        await Navigation.PushModalAsync(
            new DeviceSelectionPage(
                _discovery,
                _transfer));
    }

    private async void OnReceiveClicked(object sender, EventArgs e)
    {
        try
        {
#if ANDROID
            var hotspotInfo = await _hotspot.StartAsync();

            await DisplayAlertAsync(
                "شبکه آماده است",
                $"دستگاه فرستنده را به این شبکه وصل کن:\n\n" +
                $"نام شبکه: {hotspotInfo.Ssid}\n" +
                $"رمز شبکه: {hotspotInfo.Password}",
                "باشه");
#endif

            // دریافت‌کننده هم در شبکه محلی قابل کشف می‌شود.
            await _discovery.StartAsync();

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
#if ANDROID
            _hotspot.Stop();
#endif

            await _discovery.StopAsync();

            await DisplayAlertAsync(
                "خطا در شروع دریافت",
                ex.Message,
                "باشه");
        }
    }

    protected override void OnDisappearing()
    {
        // عمداً Discovery و Transfer را اینجا متوقف نمی‌کنیم.
        //
        // اتصال نباید به lifecycle صفحه وابسته باشد.
        //
        // توقف واقعی فقط توسط صفحه/عملیات مربوط به اتصال انجام می‌شود.

        base.OnDisappearing();
    }
}