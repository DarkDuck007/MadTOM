using Avalonia.Controls;
using MADTOM.Plugins.AudioSync.ViewModels;

namespace MADTOM.Plugins.AudioSync.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

