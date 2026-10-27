
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
    private readonly ITransferSessionService _transferSession;
    private readonly IDeviceDiscoveryService _discovery;

#if ANDROID
    private readonly LocalOnlyHotspotService _hotspot;
#endif

    public MainPage(
        MainViewModel viewModel,
        ITransferSessionService transferSession,
        IDeviceDiscoveryService discovery
#if ANDROID
        , LocalOnlyHotspotService hotspot
#endif
        )
    {
        InitializeComponent();

        _viewModel = viewModel;
        _transferSession = transferSession;
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
            await OpenDeviceSelectionAsync();
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("خطا در ارسال", ex.Message, "باشه");
        }
    }

    private async void OnManualSendClicked(object sender, EventArgs e)
    {
        try
        {
            await OpenDeviceSelectionAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("خطا", ex.Message, "باشه");
        }
    }

    private async Task OpenDeviceSelectionAsync()
    {
        await Navigation.PushModalAsync(
            new DeviceSelectionPage(
                _discovery,
                _transferSession));
    }

#if ANDROID
    private async Task StartAndroidSendAsync()
    {
        var hotspotInfo = await _hotspot.StartAsync();

        await DisplayAlertAsync(
            "شبکه آماده است",
            $"دستگاه دریافت‌کننده را به این شبکه وصل کن:\n\n" +
            $"نام شبکه: {hotspotInfo.Ssid}\n" +
            $"رمز شبکه: {hotspotInfo.Password}\n\n" +
            "سپس QR صفحه دریافت را اسکن کن.",
            "باشه");

        await Navigation.PushModalAsync(
            new ScanPage(_transferSession));
    }
#endif

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

            await _discovery.StartAsync();

            var connection = await _transferSession.StartListeningAsync(
                localDeviceName: DeviceInfo.Current.Name);

            await Navigation.PushModalAsync(
                new QrPage(
                    _transferSession,
                    connection));
        }
        catch (Exception ex)
        {
            try
            {
                await _transferSession.DisconnectAsync();
            }
            catch
            {
                // حفظ خطای اصلی
            }

#if ANDROID
            _hotspot.Stop();
#endif

            await DisplayAlertAsync(
                "خطا در شروع دریافت",
                ex.Message,
                "باشه");
        }
    }

    private async void OnScanClicked(object sender, EventArgs e)
    {
        await _viewModel.ToggleScanningAsync();
    }

    protected override void OnDisappearing()
    {
        // نشست انتقال به عمر صفحه اصلی وابسته نیست.
        base.OnDisappearing();
    }
}
