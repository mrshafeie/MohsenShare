
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using MohsenShare.Maui.Models;
using MohsenShare.Maui.Services.Discovery;
using MohsenShare.Maui.Services.Transfer;

namespace MohsenShare.Maui.Views;

public partial class DeviceSelectionPage : ContentPage
{
    private readonly IDeviceDiscoveryService _discovery;
    private readonly ITransferSessionService _transfer;

    private readonly ObservableCollection<Models.DeviceInfo> _devices = new();
    private readonly ObservableCollection<TransferResumeRecord> _transfers = new();

    private CancellationTokenSource? _queueCancellation;
    private string? _activeFileId;
    private TransferResumeRecord? _selectedTransfer;

    private bool _isBusy;
    private bool _isSubscribed;
    private bool _isClosing;

    private readonly string _outboxDirectory =
        Path.Combine(FileSystem.AppDataDirectory, "TransferOutbox");

    public DeviceSelectionPage(
        IDeviceDiscoveryService discovery,
        ITransferSessionService transfer)
    {
        InitializeComponent();

        _discovery = discovery;
        _transfer = transfer;

        DevicesCollection.ItemsSource = _devices;
        TransfersCollection.ItemsSource = _transfers;

        Directory.CreateDirectory(_outboxDirectory);

        SubscribeEvents();
    }

    private void SubscribeEvents()
    {
        if (_isSubscribed)
            return;

        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        _transfer.ProgressChanged += OnProgressChanged;
        _transfer.SessionStateChanged += OnSessionStateChanged;
        _transfer.ErrorOccurred += OnTransferError;

        _isSubscribed = true;
    }

    private void UnsubscribeEvents()
    {
        if (!_isSubscribed)
            return;

        _discovery.DeviceDiscovered -= OnDeviceDiscovered;
        _transfer.ProgressChanged -= OnProgressChanged;
        _transfer.SessionStateChanged -= OnSessionStateChanged;
        _transfer.ErrorOccurred -= OnTransferError;

        _isSubscribed = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _isClosing = false;
        SubscribeEvents();

        await RefreshDevicesAsync();
        await RefreshTransfersAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            await _discovery.StartAsync();

            var devices = _discovery.GetDevices();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _devices.Clear();

                foreach (var device in devices)
                    _devices.Add(device);

                StatusLabel.Text = _devices.Count == 0
                    ? "دستگاهی پیدا نشد؛ اطلاعات اتصال را دستی وارد کن."
                    : $"{_devices.Count} دستگاه پیدا شد.";
            });
        }
        catch (Exception ex)
        {
            await SetStatusAsync($"خطا در جست‌وجو: {ex.Message}");
        }
    }

    private async void OnRefreshDevicesClicked(
        object sender,
        EventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private void OnDeviceDiscovered(
        object? sender,
        Models.DeviceInfo device)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = _devices.FirstOrDefault(
                x => x.Id == device.Id);

            if (existing is null)
            {
                _devices.Add(device);
            }
            else
            {
                var index = _devices.IndexOf(existing);
                _devices[index] = device;
            }
        });
    }

    private void OnDeviceSelected(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not Models.DeviceInfo device)
            return;

        ManualIpEntry.Text = device.IpAddress;
        ManualPortEntry.Text = device.Port.ToString();

        // کشف دستگاه به‌تنهایی توکن احراز هویت را فراهم نمی‌کند.
        // توکن باید از صفحه دریافت دستگاه مقصد گرفته شود.
        StatusLabel.Text =
            $"دستگاه {device.Name} انتخاب شد؛ کد اتصال معتبر را وارد کن.";

        DevicesCollection.SelectedItem = null;
    }

    private async void OnConnectAndSendClicked(
        object sender,
        EventArgs e)
    {
        if (_isBusy)
            return;

        if (!IPAddress.TryParse(
                ManualIpEntry.Text?.Trim(),
                out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            await DisplayAlertAsync(
                "IP نامعتبر",
                "یک آدرس IPv4 معتبر برای دستگاه مقصد وارد کن.",
                "باشه");
            return;
        }

        if (!int.TryParse(ManualPortEntry.Text?.Trim(), out int port) ||
            port is < 1 or > 65535)
        {
            await DisplayAlertAsync(
                "پورت نامعتبر",
                "پورت اتصال باید عددی بین 1 و 65535 باشد.",
                "باشه");
            return;
        }

        string token = ManualTokenEntry.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            await DisplayAlertAsync(
                "کد اتصال لازم است",
                "کد اتصال را از صفحه دریافت دستگاه مقصد وارد کن.",
                "باشه");
            return;
        }

        var files = await FilePicker.Default.PickMultipleAsync(
            new PickOptions
            {
                PickerTitle = "فایل‌های موردنظر برای ارسال را انتخاب کن"
            });

        if (files is null)
            return;

        var selectedFiles = files.ToList();

        if (selectedFiles.Count == 0)
            return;

        SetBusy(true);
        _queueCancellation?.Dispose();
        _queueCancellation = new CancellationTokenSource();

        var stagedPaths = new List<string>();

        try
        {
            await SetStatusAsync("در حال آماده‌سازی فایل‌ها...");

            foreach (var file in selectedFiles)
            {
                _queueCancellation.Token.ThrowIfCancellationRequested();

                string safeName = Path.GetFileName(file.FileName);

                if (string.IsNullOrWhiteSpace(safeName))
                    safeName = "file";

                string stagedPath = Path.Combine(
                    _outboxDirectory,
                    $"{Guid.NewGuid():N}_{safeName}");

                await using (var source = await file.OpenReadAsync())
                await using (var destination = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(
                        destination,
                        _queueCancellation.Token);
                }

                stagedPaths.Add(stagedPath);
            }

            await SetStatusAsync("در حال برقراری اتصال امن...");

            await _transfer.ConnectAsync(
                address.ToString(),
                port,
                token,
                Environment.MachineName,
                _queueCancellation.Token);

            await SetStatusAsync(
                $"متصل شدیم؛ ارسال {stagedPaths.Count} فایل شروع شد.");

            StopQueueButton.IsEnabled = true;

            await _transfer.QueueFilesAsync(
                stagedPaths,
                _queueCancellation.Token);

            await SetStatusAsync("صف ارسال به پایان رسید.");

            await RefreshTransfersAsync();
            await CleanupCompletedStagedFilesAsync();
        }
        catch (OperationCanceledException)
        {
            await SetStatusAsync(
                "صف متوقف شد. فایل‌های ناتمام برای ادامه بعدی نگه داشته شده‌اند.");

            await RefreshTransfersAsync();
        }
        catch (Exception ex)
        {
            await SetStatusAsync($"خطا: {ex.Message}");

            await DisplayAlertAsync(
                "انتقال انجام نشد",
                $"{ex.Message}\n\nسوابق انتقال بررسی می‌شوند تا امکان ادامه فراهم بماند.",
                "باشه");

            await RefreshTransfersAsync();
        }
        finally
        {
            StopQueueButton.IsEnabled = false;
            _queueCancellation?.Dispose();
            _queueCancellation = null;

            SetBusy(false);
        }
    }

    private async void OnStopQueueClicked(
        object sender,
        EventArgs e)
    {
        if (_queueCancellation is null || _queueCancellation.IsCancellationRequested)
            return;

        bool confirm = await DisplayAlertAsync(
            "توقف صف",
            "صف ارسال متوقف می‌شود. امکان ادامه انتقال‌های ناتمام به وضعیت و اتصال ذخیره‌شده بستگی دارد. ادامه می‌دهی؟",
            "توقف",
            "انصراف");

        if (!confirm)
            return;

        _queueCancellation.Cancel();
        StopQueueButton.IsEnabled = false;

        await SetStatusAsync("درخواست توقف صف ارسال شد...");
    }

    private void OnProgressChanged(
        object? sender,
        TransferProgressChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _activeFileId = e.Transfer.FileId;

            double percentage = Math.Clamp(
                e.ProgressPercentage / 100d,
                0d,
                1d);

            TransferProgressBar.Progress = percentage;
            ProgressLabel.Text = $"{e.ProgressPercentage:F1}%";

            SpeedLabel.Text = FormatBytesPerSecond(e.BytesPerSecond);

            EtaLabel.Text = e.EstimatedRemaining.HasValue
                ? $"زمان باقی‌مانده: {e.EstimatedRemaining.Value:hh\\:mm\\:ss}"
                : "زمان باقی‌مانده: در حال محاسبه";

            StatusLabel.Text =
                $"{e.Transfer.FileName} — {e.Transfer.State}";
        });
    }

    private void OnSessionStateChanged(
        object? sender,
        TransferSessionStateChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!string.IsNullOrWhiteSpace(e.Message))
                StatusLabel.Text = e.Message;
        });
    }

    private void OnTransferError(
        object? sender,
        TransferErrorEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = $"خطای انتقال: {e.Message}";
        });
    }

    private async Task RefreshTransfersAsync()
    {
        try
        {
            var records = await _transfer.GetTransfersAsync();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _transfers.Clear();

                foreach (var record in records
                    .OrderByDescending(x => x.UpdatedAtUtc))
                {
                    _transfers.Add(record);
                }
            });
        }
        catch (Exception ex)
        {
            await SetStatusAsync(
                $"بازیابی سوابق انتقال ناموفق بود: {ex.Message}");
        }
    }

    private async void OnRefreshTransfersClicked(
        object sender,
        EventArgs e)
    {
        await RefreshTransfersAsync();
    }

    private void OnTransferSelected(
        object sender,
        SelectionChangedEventArgs e)
    {
        _selectedTransfer =
            e.CurrentSelection.FirstOrDefault() as TransferResumeRecord;

        ResumeButton.IsEnabled =
            _selectedTransfer is not null &&
            _selectedTransfer.Direction == TransferDirection.Sending &&
            _selectedTransfer.State is not TransferFileState.Completed
                and not TransferFileState.Cancelled;
    }

    private async void OnResumeClicked(
        object sender,
        EventArgs e)
    {
        if (_isBusy || _selectedTransfer is null)
            return;

        var record = _selectedTransfer;

        if (string.IsNullOrWhiteSpace(record.SourcePath) ||
            !File.Exists(record.SourcePath))
        {
            await DisplayAlertAsync(
                "فایل منبع پیدا نشد",
                "فایل اصلی برای ادامه انتقال موجود نیست. مسیر فایل را بررسی کن.",
                "باشه");
            return;
        }

        SetBusy(true);

        try
        {
            await SetStatusAsync($"در حال ادامه انتقال {record.FileName}...");

            await _transfer.ResumeAsync(record.FileId);

            await SetStatusAsync("درخواست ادامه انتقال به پایان رسید.");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(
                "ادامه انتقال ناموفق بود",
                ex.Message,
                "باشه");

            await SetStatusAsync($"ادامه انتقال انجام نشد: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshTransfersAsync();
        }
    }

    private async Task CleanupCompletedStagedFilesAsync()
    {
        var records = await _transfer.GetTransfersAsync();

        foreach (var record in records)
        {
            if (record.State != TransferFileState.Completed ||
                string.IsNullOrWhiteSpace(record.SourcePath))
            {
                continue;
            }

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(record.SourcePath);
            }
            catch
            {
                continue;
            }

            // فقط فایل‌های متعلق به پوشه خروجی خود برنامه حذف شوند.
            string root = Path.GetFullPath(_outboxDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Cleanup failed: {ex.Message}");
            }
        }
    }

    private async Task SetStatusAsync(string message)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            StatusLabel.Text = message;
        });
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;

        ConnectAndSendButton.IsEnabled = !busy;
        RefreshDevicesButton.IsEnabled = !busy;
        ResumeButton.IsEnabled = !busy && _selectedTransfer is not null &&
            _selectedTransfer.Direction == TransferDirection.Sending &&
            _selectedTransfer.State is not TransferFileState.Completed
                and not TransferFileState.Cancelled;
    }

    private static string FormatBytesPerSecond(long bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double speed = Math.Max(0, bytesPerSecond);
        int unit = 0;

        while (speed >= 1024 && unit < units.Length - 1)
        {
            speed /= 1024;
            unit++;
        }

        return $"{speed:F1} {units[unit]}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }

    private async void OnCancelClicked(
        object sender,
        EventArgs e)
    {
        if (_isBusy)
        {
            bool confirm = await DisplayAlertAsync(
                "انتقال در حال اجراست",
                "اگر صفحه را ببندی، ممکن است اتصال قطع شود. ابتدا صف را متوقف کن.",
                "بستن صفحه",
                "بازگشت");

            if (!confirm)
                return;

            _queueCancellation?.Cancel();
        }

        _isClosing = true;
        UnsubscribeEvents();

        try
        {
            await _discovery.StopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        if (Navigation.ModalStack.Contains(this))
            await Navigation.PopModalAsync();
        else if (Navigation.NavigationStack.LastOrDefault() == this)
            await Navigation.PopAsync();
    }

    protected override async void OnDisappearing()
    {
        UnsubscribeEvents();

        try
        {
            await _discovery.StopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }

        base.OnDisappearing();
    }
}
