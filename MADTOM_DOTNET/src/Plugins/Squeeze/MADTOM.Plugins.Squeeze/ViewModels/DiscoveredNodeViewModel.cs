using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SQUEEZE.Models;
using SQUEEZE.Services;

namespace SQUEEZE.ViewModels;

public partial class DiscoveredNodeViewModel : ViewModelBase
{
    private bool _isProgrammaticSelection;

    [ObservableProperty]
    private string _nodeId = "UNKNOWN";

    [ObservableProperty]
    private string _version = "1.0.0";

    [ObservableProperty]
    private string _hardwareGpu = "none";

    [ObservableProperty]
    private bool _requiresAuth;

    [ObservableProperty]
    private DateTime _lastSeen = DateTime.UtcNow;

    public ObservableCollection<DiscoveredNodeInterface> Interfaces { get; } = new();

    [ObservableProperty]
    private DiscoveredNodeInterface? _selectedInterface;

    [ObservableProperty]
    private bool _hasMultipleInterfaces;

    public bool IsUserSelected { get; set; }

    public Action<DiscoveredNodeViewModel, DiscoveredNodeInterface>? OnInterfaceSelectionChanged { get; set; }

    partial void OnSelectedInterfaceChanged(DiscoveredNodeInterface? value)
    {
        if (_isProgrammaticSelection) return;

        if (value != null)
        {
            IsUserSelected = true;
            OnInterfaceSelectionChanged?.Invoke(this, value);
        }
    }

    public void AddOrUpdateInterface(DiscoveredServer server, string? preferredInterfaceUrl = null)
    {
        LastSeen = server.LastSeen;
        if (!string.IsNullOrWhiteSpace(server.Version)) Version = server.Version;
        if (!string.IsNullOrWhiteSpace(server.HardwareGpu)) HardwareGpu = server.HardwareGpu;
        RequiresAuth = server.RequiresAuth;

        var existing = Interfaces.FirstOrDefault(i =>
            string.Equals(i.Host, server.Host, StringComparison.OrdinalIgnoreCase) &&
            i.Port == server.Port);

        DiscoveredNodeInterface item;
        if (existing != null)
        {
            existing.LastSeen = server.LastSeen;
            item = existing;
        }
        else
        {
            item = new DiscoveredNodeInterface
            {
                Host = server.Host,
                Port = server.Port,
                LastSeen = server.LastSeen
            };
            Interfaces.Add(item);
            HasMultipleInterfaces = Interfaces.Count > 1;
        }

        // Selection resolution:
        // 1. If the user already manually selected an interface, preserve their explicit choice.
        // 2. If node has a preferred/priority interface configured, and any known interface matches it, select that preferred interface (handles out-of-order latency).
        // 3. Otherwise, if nothing is selected yet, select this first discovered interface.
        if (!IsUserSelected)
        {
            if (!string.IsNullOrWhiteSpace(preferredInterfaceUrl))
            {
                var preferred = Interfaces.FirstOrDefault(i => i.MatchesUrl(preferredInterfaceUrl));
                if (preferred != null)
                {
                    SetProgrammaticSelection(preferred);
                    return;
                }
            }

            if (SelectedInterface == null)
            {
                SetProgrammaticSelection(item);
            }
        }
    }

    public void SetProgrammaticSelection(DiscoveredNodeInterface? iface)
    {
        _isProgrammaticSelection = true;
        try
        {
            SelectedInterface = iface;
        }
        finally
        {
            _isProgrammaticSelection = false;
        }
    }

    public void SetUserSelectedInterface(DiscoveredNodeInterface iface)
    {
        IsUserSelected = true;
        SelectedInterface = iface;
        OnInterfaceSelectionChanged?.Invoke(this, iface);
    }
}
