
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Transfer;

namespace MohsenShare.Maui.Views;

public partial class DeviceSelectionPage : ContentPage
{
    private readonly IDeviceDiscoveryService _discovery;
    private readonly ITransferSessionService _transferSession;
    private readonly DeviceSelectionViewModel _viewModel = new();

    private bool _isSending;
    private bool _isSubscribed;

    public DeviceSelectionPage(
        IDeviceDiscoveryService discovery,
        ITransferSessionService transferSession)
    {
        InitializeComponent();

        _discovery = discovery;
        _transferSession = transferSession;

        BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (!_isSubscribed)
        {
            _discovery.DeviceDiscovered += OnDeviceDiscovered;
            _isSubscribed = true;
        }

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

            _viewModel.Status = _viewModel.Devices.Count == 0
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

    private async void OnManualSendClicked(
        object sender,
        EventArgs e)
    {
        if (_isSending)
            return;

        string ip = ManualIpEntry.Text?.Trim() ?? string.Empty;
        string portText = ManualPortEntry.Text?.Trim() ?? string.Empty;
        string token = ManualTokenEntry.Text?.Trim() ?? string.Empty;

        if (!IPAddress.TryParse(ip, out IPAddress? address) ||
            address.AddressFamily !=
                System.Net.Sockets.AddressFamily.InterNetwork)
        {
            await DisplayAlertAsync(
                "آدرس نامعتبر",
                "یک آدرس IPv4 معتبر وارد کن.",
                "باشه");
            return;
        }

        if (!int.TryParse(portText, out int port) ||
            port is < 1 or > 65535)
        {
            await DisplayAlertAsync(
                "پورت نامعتبر",
                "پورت TCP نمایش‌داده‌شده در صفحه دریافت را وارد کن.",
                "باشه");
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await DisplayAlertAsync(
                "کد اتصال لازم است",
                "کد اتصال صفحه دریافت را وارد کن.",
                "باشه");
            return;
        }

        await SendFileToDeviceAsync(
            ip,
            port,
            token,
            "دستگاه مقصد");
    }

    private void OnDeviceSelected(
        object sender,
        SelectionChangedEventArgs e)
    {
        var device = e.CurrentSelection.FirstOrDefault()
            as Models.DeviceInfo;

        if (sender is CollectionView collectionView)
            collectionView.SelectedItem = null;

        if (device is null ||
            string.IsNullOrWhiteSpace(device.IpAddress))
        {
            return;
        }

        // پورت کشف UDP، پورت انتقال TCP نیست.
        // بنابراین فقط IP را وارد می‌کنیم.
        ManualIpEntry.Text = device.IpAddress;

        _viewModel.Status =
            $"IP دستگاه «{device.Name}» وارد شد؛ " +
            "پورت TCP و کد اتصال را از صفحه دریافت بردار.";
    }

    private async Task SendFileToDeviceAsync(
        string ip,
        int port,
        string token,
        string deviceName)
    {
        if (_isSending)
            return;

        _isSending = true;
        _viewModel.IsScanning = false;
        ManualSendButton.IsEnabled = false;

        string? tempPath = null;
        bool connected = false;

        _transferSession.ProgressChanged += OnTransferProgressChanged;

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
                fileName = "file";

            tempPath = Path.Combine(
                FileSystem.CacheDirectory,
                $"{Guid.NewGuid():N}_{fileName}");

            _viewModel.Status = $"در حال آماده‌سازی «{fileName}»...";

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

            _viewModel.Status = $"در حال اتصال به {deviceName} ({ip}:{port})...";

            await _transferSession.ConnectAsync(
                ip,
                port,
                token,
                localDeviceName: DeviceInfo.Current.Name);

            connected = true;

            _viewModel.Status = $"در حال ارسال «{fileName}»...";

            // این فراخوانی پس از پایان پردازش صف انتقال برمی‌گردد.
            await _transferSession.QueueFilesAsync(new[] { tempPath });

            await _transferSession.DisconnectAsync();
            connected = false;

            _viewModel.Status = "ارسال فایل با موفقیت انجام شد.";

            await DisplayAlertAsync(
                "ارسال موفق",
                $"فایل «{fileName}» ارسال و تأیید شد.",
                "باشه");

            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            _viewModel.Status = "ارسال انجام نشد.";

            await DisplayAlertAsync(
                "خطا در ارسال",
                $"ارسال به {deviceName} انجام نشد.\n\n{ex.Message}",
                "باشه");
        }
        finally
        {
            _transferSession.ProgressChanged -= OnTransferProgressChanged;

            if (connected)
            {
                try
                {
                    await _transferSession.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"قطع نشست انتقال: {ex.Message}");
                }
            }

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
                        $"حذف فایل موقت ناموفق بود: {ex.Message}");
                }
            }

            _isSending = false;
            ManualSendButton.IsEnabled = true;

            if (Navigation.ModalStack.Contains(this))
                _viewModel.IsScanning = true;
        }
    }

    private void OnTransferProgressChanged(
        object? sender,
        TransferProgressChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            double speedMb = e.BytesPerSecond / (1024d * 1024d);

            _viewModel.Status =
                $"انتقال {e.Transfer.FileName}: " +
                $"{e.ProgressPercentage:F1}% — {speedMb:F2} MB/s";
        });
    }

    private async void OnCancelClicked(
        object sender,
        EventArgs e)
    {
        if (_isSending)
        {
            await DisplayAlertAsync(
                "انتقال در حال انجام",
                "ابتدا اجازه بده عملیات ارسال تمام شود.",
                "باشه");
            return;
        }

        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        if (_isSubscribed)
        {
            _discovery.DeviceDiscovered -= OnDeviceDiscovered;
            _isSubscribed = false;
        }

        _viewModel.IsScanning = false;

        base.OnDisappearing();
    }

    private sealed class DeviceSelectionViewModel : INotifyPropertyChanged
    {
        private bool _isScanning;
        private string _status = "آماده جست‌وجو";

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
            var existing = Devices.FirstOrDefault(x => x.Id == device.Id);

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
