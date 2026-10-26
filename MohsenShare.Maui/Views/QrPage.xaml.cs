
using MohsenShare.Maui.Services.Transfer;

namespace MohsenShare.Maui.Views;

public partial class QrPage : ContentPage
{
    private readonly LocalTransferService _transfer;
    private readonly Action? _stopNetwork;
    private bool _stopped;

    public QrPage(
        LocalTransferService transfer,
        byte[] qrPng,
        string address,
        string token,
        Action? stopNetwork = null)
    {
        InitializeComponent();

        _transfer = transfer;
        _stopNetwork = stopNetwork;

        QrImage.Source = ImageSource.FromStream(
            () => new MemoryStream(qrPng));

        AddressLabel.Text = address;
        TokenLabel.Text = token;

        _transfer.FileReceived += OnFileReceived;
    }

    private async void OnFileReceived(object? sender, string path)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await DisplayAlertAsync(
                "دریافت موفق",
                $"فایل دریافت شد:\n{Path.GetFileName(path)}\n\n" +
                $"محل ذخیره:\n{path}",
                "باشه");
        });
    }

    private async void OnStopClicked(object sender, EventArgs e)
    {
        await StopAndCloseAsync();
    }

    private async Task StopAndCloseAsync()
    {
        if (_stopped)
            return;

        _stopped = true;

        _transfer.FileReceived -= OnFileReceived;

        await _transfer.StopAsync();
        _stopNetwork?.Invoke();

        await Navigation.PopModalAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        if (_stopped)
            return;

        _stopped = true;

        _transfer.FileReceived -= OnFileReceived;

        await _transfer.StopAsync();
        _stopNetwork?.Invoke();
    }
}
