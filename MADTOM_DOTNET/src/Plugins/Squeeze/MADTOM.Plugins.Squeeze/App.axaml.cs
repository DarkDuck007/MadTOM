using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SQUEEZE.ViewModels;
using SQUEEZE.Views;

namespace SQUEEZE;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private static MainViewModel? _cachedMainViewModel;

    public override void OnFrameworkInitializationCompleted()
    {
        _cachedMainViewModel ??= new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _cachedMainViewModel,
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = _cachedMainViewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}