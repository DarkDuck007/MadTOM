using Avalonia.Controls;
using MADTOM.Plugins.NoxAI.ViewModels;

namespace MADTOM.Plugins.NoxAI.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

