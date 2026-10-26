using MohsenShare.Maui.Models;
using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Transfer;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MohsenShare.Maui.Views;

public partial class DeviceSelectionPage : ContentPage
{
    private readonly IDeviceDiscoveryService _discovery;
    private readonly LocalTransferService _transfer;

    private readonly DeviceSelectionViewModel _viewModel;

    private bool _isSending;

    public DeviceSelectionPage(
        IDeviceDiscoveryService discovery,
        LocalTransferService transfer)
    {
        InitializeComponent();

        _discovery = discovery;
        _transfer = transfer;

        _viewModel = new DeviceSelectionViewModel();

        BindingContext = _viewModel;

        _discovery.DeviceDiscovered += OnDeviceDiscovered;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            _viewModel.Devices.Clear();

            foreach (var device in _discovery.GetDevices())
                _viewModel.AddOrUpdate(device);

            _viewModel.IsScanning = true;
            _viewModel.Status = "در حال جستجوی دستگاه‌های نزدیک...";

            await _discovery.StartAsync();

            foreach (var device in _discovery.GetDevices())
                _viewModel.AddOrUpdate(device);

            _viewModel.Status =
                _viewModel.Devices.Count == 0
                    ? "در انتظار پیدا شدن دستگاه..."
                    : $"{_viewModel.Devices.Count} دستگاه پیدا شد";
        }
        catch (Exception ex)
        {
            _viewModel.IsScanning = false;
            _viewModel.Status = $"خطا: {ex.Message}";
        }
    }

    private void OnDeviceDiscovered(
        object? sender,
        Models.DeviceInfo device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _viewModel.AddOrUpdate(device);

            _viewModel.Status =
                $"{_viewModel.Devices.Count} دستگاه پیدا شد";
        });
    }

    private async void OnDeviceSelected(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSending)
            return;

        var device = e.CurrentSelection.FirstOrDefault()
            as Models.DeviceInfo;

        if (device is null)
            return;

        ((CollectionView)sender).SelectedItem = null;

        if (string.IsNullOrWhiteSpace(device.IpAddress))
        {
            await DisplayAlertAsync(
                "خطا",
                "آدرس دستگاه مقصد دریافت نشد.",
                "باشه");

            return;
        }

        _isSending = true;
        _viewModel.IsScanning = false;
        _viewModel.Status = "دستگاه انتخاب شد...";

        try
        {
            var file = await FilePicker.Default.PickAsync();

            if (file is null)
            {
                _isSending = false;
                _viewModel.IsScanning = true;
                _viewModel.Status = "در حال جستجوی دستگاه‌های نزدیک...";
                return;
            }

            string fileName = Path.GetFileName(file.FileName);

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "file";

            string tempPath = Path.Combine(
                FileSystem.CacheDirectory,
                $"{Guid.NewGuid():N}_{fileName}");

            try
            {
                _viewModel.Status =
                    $"در حال آماده‌سازی «{fileName}»...";

                await using (Stream source =
                             await file.OpenReadAsync())

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

                _viewModel.Status =
                    $"در حال ارسال به «{device.Name}»...";

                // در PC ↔ PC، گیرنده روی همان پورت انتقال
                // گوش می‌دهد و Discovery فقط آدرس دستگاه را پیدا می‌کند.
                await _transfer.SendFileAsync(
                    device.IpAddress,
                    LocalTransferService.TransferPort,
                    string.Empty,
                    tempPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            await DisplayAlertAsync(
                "ارسال موفق",
                $"فایل «{fileName}» با موفقیت ارسال شد.",
                "باشه");

            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(
                "خطا در ارسال",
                ex.Message,
                "باشه");

            _isSending = false;
            _viewModel.IsScanning = true;
            _viewModel.Status =
                "دوباره یک دستگاه را انتخاب کن.";
        }
    }

    private async void OnCancelClicked(
        object sender,
        EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        _discovery.DeviceDiscovered -= OnDeviceDiscovered;

        _viewModel.IsScanning = false;

        base.OnDisappearing();
    }

    private sealed class DeviceSelectionViewModel
        : INotifyPropertyChanged
    {
        private bool _isScanning;
        private string _status = "آماده جستجو";

        public ObservableCollection<Models.DeviceInfo> Devices { get; } = new();

        public bool IsScanning
        {
            get => _isScanning;
            set
            {
                if (_isScanning == value)
                    return;

                _isScanning = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;

                _status = value;
                OnPropertyChanged();
            }
        }

        public void AddOrUpdate(Models.DeviceInfo device)
        {
            var existing = Devices.FirstOrDefault(
                x => x.Id == device.Id);

            if (existing is null)
            {
                Devices.Add(device);
                return;
            }

            int index = Devices.IndexOf(existing);

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
    }
}