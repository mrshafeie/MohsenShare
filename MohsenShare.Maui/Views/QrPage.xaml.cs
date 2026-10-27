
using MohsenShare.Maui.Services.Transfer;

namespace MohsenShare.Maui.Views;

public partial class QrPage : ContentPage
{
    private readonly ITransferSessionService _transferSession;
    private bool _stopped;

    public QrPage(
        ITransferSessionService transferSession,
        TransferConnectionInfo connection)
    {
        InitializeComponent();

        _transferSession = transferSession;

        QrImage.Value = connection.CreateQrPayload();

        AddressLabel.Text =
            $"{connection.IpAddress}:{connection.Port}";

        TokenLabel.Text = connection.Token;

        _transferSession.FileReceived += OnFileReceived;
        _transferSession.ErrorOccurred += OnTransferError;
    }

    private async void OnFileReceived(
        object? sender,
        TransferFileReceivedEventArgs e)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await DisplayAlertAsync(
                "دریافت موفق",
                $"فایل: {e.FileName}\n\n" +
                $"حجم: {e.Length:N0} بایت\n\n" +
                $"محل ذخیره:\n{e.Location}",
                "باشه");
        });
    }

    private async void OnTransferError(
        object? sender,
        TransferErrorEventArgs e)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await DisplayAlertAsync(
                "خطای انتقال",
                e.Message,
                "باشه");
        });
    }

    private async void OnStopClicked(
        object sender,
        EventArgs e)
    {
        if (_stopped)
            return;

        _stopped = true;

        _transferSession.FileReceived -= OnFileReceived;
        _transferSession.ErrorOccurred -= OnTransferError;

        try
        {
            await _transferSession.DisconnectAsync();
        }
        finally
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
    }

    protected override void OnDisappearing()
    {
        _transferSession.FileReceived -= OnFileReceived;
        _transferSession.ErrorOccurred -= OnTransferError;

        base.OnDisappearing();
    }
}
