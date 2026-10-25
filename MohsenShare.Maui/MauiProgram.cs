using Microsoft.Extensions.Logging;
using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Transfer;
using MohsenShare.Maui.ViewModels;
using ZXing.Net.Maui.Controls;

namespace MohsenShare.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>().UseBarcodeReader()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });
            builder.Services.AddSingleton<IDeviceDiscoveryService, UdpDeviceDiscoveryService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddSingleton<LocalTransferService>();
#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
