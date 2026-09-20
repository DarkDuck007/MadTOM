using Avalonia.Controls;
using MADTOM.Plugins.AndroidToolkit.ViewModels;

namespace MADTOM.Plugins.AndroidToolkit.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

