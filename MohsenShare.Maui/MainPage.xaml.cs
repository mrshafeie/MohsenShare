using MohsenShare.Maui.ViewModels;

namespace MohsenShare.Maui;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    private async void OnScanClicked(
        object sender,
        EventArgs e)
    {
        await _viewModel.ToggleScanningAsync();
    }

    protected override async void OnDisappearing()
    {
        await _viewModel.StopScanningAsync();
        base.OnDisappearing();
    }
   
}