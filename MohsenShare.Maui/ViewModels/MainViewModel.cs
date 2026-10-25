using MohsenShare.Maui.Services.Discovery;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MohsenShare.Maui.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDeviceDiscoveryService _discoveryService;

    private bool _isScanning;
    private string _status = "آماده جستجوی دستگاه‌ها";

    public ObservableCollection<Models.DeviceInfo> Devices { get; } = new();

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (_isScanning == value)
                return;

            _isScanning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    public string ScanButtonText =>
        IsScanning ? "توقف جستجو" : "پیدا کردن دستگاه‌ها";

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;

            _status = value;
            OnPropertyChanged();
        }
    }

    public MainViewModel(IDeviceDiscoveryService discoveryService)
    {
        _discoveryService = discoveryService;
        _discoveryService.DeviceDiscovered += OnDeviceDiscovered;
    }

    public async Task ToggleScanningAsync()
    {
        if (IsScanning)
        {
            await _discoveryService.StopAsync();
            IsScanning = false;
            Status = "جستجو متوقف شد";
            return;
        }

        try
        {
            Devices.Clear();

            IsScanning = true;
            Status = "در حال جستجوی دستگاه‌های نزدیک...";

            await _discoveryService.StartAsync();

            foreach (var device in _discoveryService.GetDevices())
                AddOrUpdateDevice(device);
        }
        catch (Exception ex)
        {
            IsScanning = false;
            Status = $"خطا در شروع جستجو: {ex.Message}";
        }
    }

    private void OnDeviceDiscovered(object? sender, Models.DeviceInfo device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AddOrUpdateDevice(device);
            Status = $"{Devices.Count} دستگاه پیدا شد";
        });
    }

    private void AddOrUpdateDevice(Models.DeviceInfo device)
    {
        var existing = Devices.FirstOrDefault(x => x.Id == device.Id);

        if (existing is null)
        {
            Devices.Add(device);
            return;
        }

        existing.Name = device.Name;
        existing.IpAddress = device.IpAddress;
        existing.Port = device.Port;
        existing.Platform = device.Platform;

        // ObservableCollection تغییر خواص داخلی مدل را خودکار اعلام نمی‌کند.
        // در نسخه فعلی برای جلوگیری از پیچیدگی، آیتم را جایگزین می‌کنیم.
        var index = Devices.IndexOf(existing);
        Devices[index] = device;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
    public async Task StopScanningAsync()
    {
        if (!IsScanning)
            return;

        await _discoveryService.StopAsync();

        IsScanning = false;
        Status = "جستجو متوقف شد";
    }
}