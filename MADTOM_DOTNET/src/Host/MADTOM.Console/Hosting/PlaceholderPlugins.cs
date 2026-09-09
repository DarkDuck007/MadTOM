using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MADTOM.PluginContracts;

namespace MADTOM.Console.Hosting;

/// <summary>
/// Placeholder plugin representing MADTOM Sysadmin.
/// Demonstrates multi-plugin visual mounting and isolated lifecycle.
/// </summary>
public sealed class SysadminPlaceholderPluginModule : IPluginModule
{
    public string Id => "sysadmin";
    public string DisplayName => "Sysadmin";
    public string Description => "Remote host orchestration, SSH sessions, service managers, and journalctl analysis";
    public string IconGlyph => "🖥️";
    public string Category => "Systems";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 20;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        hostContext.Lexicons.RegisterLexicon(Id, "goose", new Dictionary<string, string>
        {
            ["moduleTitle"] = "HONK SYSTEM ADMINISTRATION CONSOLE",
            ["status"] = "All nodes listening on port 22. Geese ready to deploy."
        });
        return Task.CompletedTask;
    }

    public Control CreateView()
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#080D1A")),
            Padding = new Avalonia.Thickness(32),
            Child = new StackPanel
            {
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "🖥️ MADTOM Sysadmin",
                        FontSize = 26,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "Remote SSH cluster manager, systemd units, container runtimes, and distributed process orchestrator.",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#0F172A")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#1E293B")),
                        BorderThickness = new Avalonia.Thickness(1),
                        CornerRadius = new Avalonia.CornerRadius(8),
                        Padding = new Avalonia.Thickness(20),
                        Margin = new Avalonia.Thickness(0, 16, 0, 0),
                        Child = new TextBlock
                        {
                            Text = "⚡ Module architecture ready for SSH.NET and D-Bus integration.",
                            Foreground = new SolidColorBrush(Color.Parse("#22C55E")),
                            FontFamily = FontFamily.Parse("Consolas, monospace")
                        }
                    }
                }
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> CanCloseAsync() => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Placeholder plugin representing MADTOM ESC (Embedded Systems Controller).
/// </summary>
public sealed class EscPlaceholderPluginModule : IPluginModule
{
    public string Id => "esc";
    public string DisplayName => "Embedded ESC";
    public string Description => "Embedded Systems Controller: CAN bus analyzer, serial link debugger, and hardware signal triggers";
    public string IconGlyph => "⚡";
    public string Category => "Hardware";
    public Version Version => new(1, 0, 0);
    public int OrderWeight => 30;

    public Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Control CreateView()
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#080D1A")),
            Padding = new Avalonia.Thickness(32),
            Child = new StackPanel
            {
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "⚡ MADTOM ESC (Embedded Systems Controller)",
                        FontSize = 26,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#F59E0B")),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "Real-time CAN-bus frame logger, UART/SPI serial oscilloscope, and GPIO hardware triggers.",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#0F172A")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#1E293B")),
                        BorderThickness = new Avalonia.Thickness(1),
                        CornerRadius = new Avalonia.CornerRadius(8),
                        Padding = new Avalonia.Thickness(20),
                        Margin = new Avalonia.Thickness(0, 16, 0, 0),
                        Child = new TextBlock
                        {
                            Text = "🔧 Ready for System.IO.Ports and SocketCAN kernel bindings.",
                            Foreground = new SolidColorBrush(Color.Parse("#F59E0B")),
                            FontFamily = FontFamily.Parse("Consolas, monospace")
                        }
                    }
                }
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> CanCloseAsync() => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
