using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MADTOM.Console.Hosting;
using MADTOM.Console.ViewModels;
using MADTOM.Console.Views;
using MadTOM;

namespace MADTOM.Console;

public partial class App : Application
{
    private PluginManager? _pluginManager;
    private TrayIcon? _trayIcon;
    private readonly NativeMenu _trayMenu = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var hostContext = new ConsoleHostContext();
            _pluginManager = new PluginManager(hostContext);

            // Register primary Telemetry plugin
            var telemetryModule = new TelemetryPluginModule();
            _pluginManager.RegisterModuleAsync(telemetryModule).GetAwaiter().GetResult();

            // Register sample plugins for multi-module switching
            _pluginManager.RegisterModuleAsync(new SysadminPlaceholderPluginModule()).GetAwaiter().GetResult();
            _pluginManager.RegisterModuleAsync(new EscPlaceholderPluginModule()).GetAwaiter().GetResult();

            var viewModel = new ConsoleMainViewModel(hostContext, _pluginManager);

            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.MainWindow = mainWindow;

            // Setup System Tray Icon & Decoupled Menu
            SetupTrayIcon(desktop, mainWindow, hostContext);

            desktop.Exit += async (_, _) =>
            {
                if (_pluginManager != null)
                {
                    await _pluginManager.DisposeAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow,
        ConsoleHostContext hostContext)
    {
        _trayIcon = new TrayIcon
        {
            ToolTipText = "MADTOM Console",
            IsVisible = true,
            Menu = _trayMenu
        };

        _trayIcon.Clicked += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (mainWindow.IsVisible && mainWindow.WindowState != WindowState.Minimized)
                {
                    mainWindow.Activate();
                }
                else
                {
                    mainWindow.RestoreFromTray();
                }
            });
        };

        // Try loading application icon
        try
        {
            var iconUri = new Uri("avares://MADTOM.Console/Assets/madtom-icon.ico");
            if (AssetLoader.Exists(iconUri))
            {
                using var stream = AssetLoader.Open(iconUri);
                var windowIcon = new WindowIcon(stream);
                _trayIcon.Icon = windowIcon;
                mainWindow.Icon = windowIcon;
            }
        }
        catch
        {
            // Non-fatal if icon resource load fails on certain platforms
        }

        RebuildTrayMenu(mainWindow, hostContext, desktop);

        hostContext.Tray.SectionsChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => RebuildTrayMenu(mainWindow, hostContext, desktop));
        };

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void RebuildTrayMenu(
        MainWindow mainWindow,
        ConsoleHostContext hostContext,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_trayIcon == null)
            return;

        var desiredItems = new List<NativeMenuItemBase>();

        // Top Section: Console Owned
        desiredItems.Add(new NativeMenuItem("🖥️ Open MADTOM Console")
        {
            Command = new RelayCommand(() => mainWindow.RestoreFromTray())
        });

        // Middle Sections: Plugin Contributed (Telemetry, etc.)
        var sections = hostContext.Tray.GetSections();
        if (sections.Count > 0)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var items = section.GetItems();
                if (items.Count == 0)
                    continue;

                desiredItems.Add(new NativeMenuItemSeparator());

                // Compact, non-intrusive plugin header (disabled for muted color)
                string pluginName = !string.IsNullOrWhiteSpace(section.PluginName)
                    ? section.PluginName
                    : section.SectionId;

                desiredItems.Add(new NativeMenuItem($"— {pluginName} —")
                {
                    IsEnabled = false
                });

                foreach (var item in items)
                {
                    if (!item.IsVisible)
                        continue;

                    if (item.IsSeparator)
                    {
                        desiredItems.Add(new NativeMenuItemSeparator());
                        continue;
                    }

                    var nativeItem = new NativeMenuItem(item.Text)
                    {
                        IsEnabled = item.IsEnabled
                    };

                    if (item.Action != null)
                    {
                        nativeItem.Command = new RelayCommand(item.Action);
                    }

                    desiredItems.Add(nativeItem);
                }
            }
        }

        // Bottom Section: Console Owned
        desiredItems.Add(new NativeMenuItemSeparator());
        desiredItems.Add(new NativeMenuItem("✕ Quit MADTOM")
        {
            Command = new RelayCommand(() =>
            {
                mainWindow.CloseForReal();
                desktop.Shutdown();
            })
        });

        // In-place update if item count and types match to avoid DBusMenu re-registration and icon flicker
        bool canUpdateInPlace = (_trayMenu.Items.Count == desiredItems.Count);
        if (canUpdateInPlace)
        {
            for (int i = 0; i < desiredItems.Count; i++)
            {
                if (_trayMenu.Items[i].GetType() != desiredItems[i].GetType())
                {
                    canUpdateInPlace = false;
                    break;
                }
            }
        }

        if (canUpdateInPlace)
        {
            for (int i = 0; i < desiredItems.Count; i++)
            {
                if (_trayMenu.Items[i] is NativeMenuItem existing && desiredItems[i] is NativeMenuItem desired)
                {
                    if (existing.Header != desired.Header)
                    {
                        existing.Header = desired.Header;
                    }
                    if (existing.IsEnabled != desired.IsEnabled)
                    {
                        existing.IsEnabled = desired.IsEnabled;
                    }
                    existing.Command = desired.Command;
                }
            }
        }
        else
        {
            _trayMenu.Items.Clear();
            foreach (var item in desiredItems)
            {
                _trayMenu.Items.Add(item);
            }
        }
    }
}
