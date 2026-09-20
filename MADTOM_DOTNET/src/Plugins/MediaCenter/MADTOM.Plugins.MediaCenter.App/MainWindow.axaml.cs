using Avalonia.Controls;
using MADTOM.Plugins.MediaCenter.ViewModels;

namespace MADTOM.Plugins.MediaCenter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

