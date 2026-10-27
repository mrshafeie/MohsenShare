
using Microsoft.Extensions.Logging;
using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Storage;
using MohsenShare.Maui.Services.Transfer;
using MohsenShare.Maui.ViewModels;
using ZXing.Net.Maui.Controls;

#if ANDROID
using MohsenShare.Maui.Platforms.Android;
#endif

namespace MohsenShare.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont(
                    "OpenSans-Regular.ttf",
                    "OpenSansRegular");

                fonts.AddFont(
                    "OpenSans-Semibold.ttf",
                    "OpenSansSemibold");
            });

        // Device discovery
        builder.Services.AddSingleton<
            IDeviceDiscoveryService,
            UdpDeviceDiscoveryService>();

        // Existing application services
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddSingleton<LocalTransferService>();

        // Transfer persistence and partial-file storage
        builder.Services.AddSingleton<TransferResumeStore>();
        builder.Services.AddSingleton<TransferPartialFileStore>();

        // Public destination storage
        builder.Services.AddSingleton<ReceivedFileStorageService>();

        // Transfer session engine
        builder.Services.AddSingleton<
            ITransferSessionService,
            TransferSessionService>();

#if ANDROID
        builder.Services.AddSingleton<LocalOnlyHotspotService>();
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
