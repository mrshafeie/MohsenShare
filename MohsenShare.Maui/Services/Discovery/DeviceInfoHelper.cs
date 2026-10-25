namespace MohsenShare.Maui.Services.Discovery;

public static class DeviceInfoHelper
{
    public static string GetDeviceName()
    {
#if ANDROID
        return Android.OS.Build.Model ?? "Android Device";
#elif WINDOWS
        return Environment.MachineName;
#else
        return Environment.MachineName;
#endif
    }

    public static string GetPlatform()
    {
#if ANDROID
        return "Android";
#elif WINDOWS
        return "Windows";
#else
        return "Unknown";
#endif
    }
}