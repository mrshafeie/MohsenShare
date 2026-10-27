using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Transfer;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
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
            {
                _viewModel.AddOrUpdate(device);
            }

            _viewModel.IsScanning = true;
            _viewModel.Status =
                "در حال جستجوی دستگاه‌های نزدیک...";

            await _discovery.StartAsync();

            foreach (var device in _discovery.GetDevices())
            {
                _viewModel.AddOrUpdate(device);
            }

            _viewModel.Status =
                _viewModel.Devices.Count == 0
                    ? "در انتظار پیدا شدن دستگاه..."
                    : $"{_viewModel.Devices.Count} دستگاه پیدا شد";
        }
        catch (Exception ex)
        {
            _viewModel.IsScanning = false;
            _viewModel.Status = $"خطا در جست‌وجو: {ex.Message}";
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

    /// <summary>
    /// ارسال فایل به گوشی با استفاده از IP و توکن واردشده.
    /// </summary>
    private async void OnManualSendClicked(
        object sender,
        EventArgs e)
    {
        if (_isSending)
            return;

        string ip = ManualIpEntry.Text?.Trim() ?? string.Empty;
        string token = ManualTokenEntry.Text?.Trim() ?? string.Empty;

        if (!IPAddress.TryParse(ip, out IPAddress? address) ||
            address.AddressFamily !=
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            await DisplayAlertAsync(
                "آدرس نامعتبر",
                "لطفاً آدرس IPv4 گوشی را به‌درستی وارد کن.",
                "باشه");

            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await DisplayAlertAsync(
                "کد اتصال لازم است",
                "کد اتصالی را که در صفحه دریافت گوشی نمایش داده می‌شود وارد کن.",
                "باشه");

            return;
        }

        await SendFileToDeviceAsync(
            ip,
            token,
            "گوشی مقصد");
    }

    /// <summary>
    /// ارسال از مسیر دستگاه‌های پیدا‌شده.
    /// مسیر جست‌وجوی خودکار موجود حفظ شده است.
    /// </summary>
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

        await SendFileToDeviceAsync(
            device.IpAddress,
            string.Empty,
            device.Name);
    }

    private async Task SendFileToDeviceAsync(
        string ip,
        string token,
        string deviceName)
    {
        if (_isSending)
            return;

        _isSending = true;
        _viewModel.IsScanning = false;
        ManualSendButton.IsEnabled = false;

        string? tempPath = null;

        try
        {
            _viewModel.Status = "در انتظار انتخاب فایل...";

            var file = await FilePicker.Default.PickAsync();

            if (file is null)
            {
                _viewModel.Status = "ارسال لغو شد.";
                return;
            }

            string fileName = Path.GetFileName(file.FileName);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "file";
            }

            tempPath = Path.Combine(
                FileSystem.CacheDirectory,
                $"{Guid.NewGuid():N}_{fileName}");

            _viewModel.Status =
                $"در حال آماده‌سازی «{fileName}»...";

            await using (Stream source = await file.OpenReadAsync())
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
                $"در حال اتصال به {deviceName} ({ip})...";

            // در مسیر دستی، توکن از گوشی وارد می‌شود.
            // در مسیر خودکار، رفتار قبلی حفظ شده است.
            await _transfer.SendFileAsync(
                ip,
                LocalTransferService.TransferPort,
                token,
                tempPath);

            _viewModel.Status = "ارسال فایل با موفقیت انجام شد.";

            await DisplayAlertAsync(
                "ارسال موفق",
                $"فایل «{fileName}» با موفقیت ارسال شد.",
                "باشه");

            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            _viewModel.Status = "ارسال انجام نشد.";

            await DisplayAlertAsync(
                "خطا در ارسال",
                $"ارسال فایل به {deviceName} انجام نشد.\n\n{ex.Message}",
                "باشه");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) &&
                File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MohsenShare] حذف فایل موقت ناموفق بود: {ex.Message}");
                }
            }

            _isSending = false;
            ManualSendButton.IsEnabled = true;

            if (Navigation.ModalStack.Contains(this))
            {
                _viewModel.IsScanning = true;
            }
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
        private string _status = "آماده جست‌وجو";

        public ObservableCollection<Models.DeviceInfo> Devices { get; }
            = new();

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