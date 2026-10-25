
using MohsenShare.Maui.Services.Transfer;

namespace MohsenShare.Maui.Views;

public partial class QrPage : ContentPage
{
    private readonly LocalTransferService _transfer;
    private bool _stopped;

    public QrPage(
        LocalTransferService transfer,
        byte[] qrPng,
        string address,
        string token)
    {
        InitializeComponent();

        _transfer = transfer;

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
                $"فایل دریافت شد:\n{Path.GetFileName(path)}\n\nمحل ذخیره:\n{path}",
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

        await Navigation.PopModalAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        if (!_stopped)
        {
            _stopped = true;

            _transfer.FileReceived -= OnFileReceived;

            await _transfer.StopAsync();
        }
    }
}
