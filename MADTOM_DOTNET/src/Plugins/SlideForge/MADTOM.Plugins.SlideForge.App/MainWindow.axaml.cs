using Avalonia.Controls;
using MADTOM.Plugins.SlideForge.ViewModels;

namespace MADTOM.Plugins.SlideForge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }
}
