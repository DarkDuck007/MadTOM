using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Platform;

namespace MadTOM.Localization;

public sealed class LexiconService : ILexiconService
{
    private static readonly Lazy<LexiconService> _lazyInstance = new(() => new LexiconService());
    public static LexiconService Instance => _lazyInstance.Value;

    private readonly Dictionary<string, Dictionary<string, string>> _cachedPacks = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _activeTokens = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentPack { get; private set; } = "goose";
    public IReadOnlyList<string> AvailablePacks => new List<string>(_cachedPacks.Keys);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LexiconChanged;

    public string this[string key]
    {
        get
        {
            if (_activeTokens.TryGetValue(key, out var translation))
            {
                return translation;
            }
            return $"!{key}!"; // Missing key sentinel
        }
    }

    public LexiconService()
    {
        // Load default persona pack
        LoadLexicon("goose");
    }

    public void LoadLexicon(string packName)
    {
        if (_cachedPacks.TryGetValue(packName, out var cached))
        {
            _activeTokens = cached;
            CurrentPack = packName;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            OnPropertyChanged(nameof(CurrentPack));
            LexiconChanged?.Invoke(this, packName);
            return;
        }

        // Try load from Avalonia assets
        try
        {
            var uri = new Uri($"avares://MadTOM/Assets/Lexicons/{packName.ToLowerInvariant()}.json");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed != null)
                {
                    _cachedPacks[packName] = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                    _activeTokens = _cachedPacks[packName];
                    CurrentPack = packName;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                    OnPropertyChanged(nameof(CurrentPack));
                    OnPropertyChanged(nameof(AvailablePacks));
                    LexiconChanged?.Invoke(this, packName);
                    return;
                }
            }
        }
        catch
        {
            // If headless or asset loader unavailable, fallback below
        }

        // Provide fallback tokens if asset could not be loaded
        var fallback = CreateFallbackLexicon(packName);
        _cachedPacks[packName] = fallback;
        _activeTokens = fallback;
        CurrentPack = packName;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        OnPropertyChanged(nameof(CurrentPack));
        OnPropertyChanged(nameof(AvailablePacks));
        LexiconChanged?.Invoke(this, packName);
    }

    public void ImportLexicon(string packName, string jsonOrFilePath)
    {
        string json = jsonOrFilePath;
        if (File.Exists(jsonOrFilePath))
        {
            json = File.ReadAllText(jsonOrFilePath);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (parsed == null)
        {
            throw new InvalidOperationException($"Invalid lexicon JSON for pack: {packName}");
        }

        RegisterLexicon(packName, parsed);
        LoadLexicon(packName);
    }

    public void RegisterLexicon(string packName, Dictionary<string, string> tokens)
    {
        _cachedPacks[packName] = new Dictionary<string, string>(tokens, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(AvailablePacks));
    }

    private static Dictionary<string, string> CreateFallbackLexicon(string packName)
    {
        if (packName.Equals("standard", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fleetTitle"] = "Fleet Infrastructure Matrix",
                ["fleetSubtitle"] = "Ultra-compact directional TWAMP transit, aggregated hardware sparklines, and viewport-filling micro-core strips.",
                ["level1Badge"] = "Fleet Observation Level 1",
                ["filterBaremetal"] = "Dedicated Hosts",
                ["filterVm"] = "Virtual Machines",
                ["backToFleet"] = "Back to Fleet Matrix",
                ["tabMetrics"] = "Historical Telemetry",
                ["tabProcesses"] = "Process Manager",
                ["tabLogs"] = "System Journal",
                ["tabFlight"] = "Network & Topology",
                ["coreMatrixTitle"] = "Logical Processor Matrix",
                ["directionalTitle"] = "TWAMP Directional Transit & Jitter",
                ["legendUpstream"] = "Forward (↑)",
                ["legendDownstream"] = "Reverse (↓)",
                ["legendAsymmetry"] = "Asymmetry (Δ)",
                ["nflogRadar"] = "Firewall Drop Radar (NFLOG)",
                ["processKill"] = "SIGKILL",
                ["processTerm"] = "SIGTERM",
                ["statHardware"] = "Processor Architecture",
                ["statTwampLatency"] = "Directional TWAMP",
                ["asymmetry"] = "Path Asymmetry",
                ["baremetalBadge"] = "Dedicated Host",
                ["vmBadge"] = "Virtual Machine",
                ["navSectionViews"] = "Telemetry Views",
                ["navSectionNodes"] = "Active Nodes",
                ["navFleetMatrix"] = "Fleet Matrix",
                ["navGlobalRadar"] = "Global Network Radar",
                ["radarTitle"] = "Global Network Radar",
                ["radarSubtitle"] = "Macro routing distribution, autonomous system (ASN) peering paths, and global TWAMP vector transit.",
                ["radarBadge"] = "Global Transit & Threat Intel"
            };
        }

        if (packName.Equals("feline", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fleetTitle"] = "The Clowder Domain 🐱",
                ["fleetSubtitle"] = "Whisker vibration arrays that fill the room, pounce vs retreat latency checks, and litter telemetry.",
                ["level1Badge"] = "Level 1: The Clowder",
                ["filterBaremetal"] = "Big Toms",
                ["filterVm"] = "Kittens",
                ["backToFleet"] = "Back to The Clowder",
                ["tabMetrics"] = "Stalking Statistics",
                ["tabProcesses"] = "Litter Sifting",
                ["tabLogs"] = "Scratching Post",
                ["tabFlight"] = "Zoomies Track & Radar",
                ["coreMatrixTitle"] = "Whisker Array",
                ["directionalTitle"] = "Pounce vs Retreat Transit Dynamics",
                ["legendUpstream"] = "Pounce (↑)",
                ["legendDownstream"] = "Swat (↓)",
                ["legendAsymmetry"] = "Ear Twitch (Δ)",
                ["nflogRadar"] = "Hairball Ejection Zone",
                ["processKill"] = "Full-Body Pounce",
                ["processTerm"] = "Paw Tap Warning",
                ["statHardware"] = "Tomcat Reflexes",
                ["statTwampLatency"] = "Pounce Velocity",
                ["asymmetry"] = "Paws Off-Center",
                ["baremetalBadge"] = "Big Tom",
                ["vmBadge"] = "Kitten",
                ["navSectionViews"] = "Territories",
                ["navSectionNodes"] = "Clowder Roster",
                ["navFleetMatrix"] = "The Clowder",
                ["navGlobalRadar"] = "Global Zoomies Radar",
                ["radarTitle"] = "Global Zoomies Radar 🐱",
                ["radarSubtitle"] = "International laser-dot vectors, territorial purr transit, and hairball interception radar.",
                ["radarBadge"] = "Global Territory Map"
            };
        }

        // Default Goose
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fleetTitle"] = "The Pond Overview 🪿",
            ["fleetSubtitle"] = "Feather-light directional honk transit, muscle fiber strips that fill the pond, and V-formation flight vitals.",
            ["level1Badge"] = "Level 1: The Gaggle",
            ["filterBaremetal"] = "Ganders Only",
            ["filterVm"] = "Goslings Only",
            ["backToFleet"] = "Back to The Pond",
            ["tabMetrics"] = "Flight Vitals",
            ["tabProcesses"] = "Pecking Order",
            ["tabLogs"] = "Honk Ledger",
            ["tabFlight"] = "Flight Path & Radar",
            ["coreMatrixTitle"] = "Flock Muscle Fibers",
            ["directionalTitle"] = "Headwind vs Tailwind Honk Drift",
            ["legendUpstream"] = "Headwind (↑)",
            ["legendDownstream"] = "Tailwind (↓)",
            ["legendAsymmetry"] = "Imbalance (Δ)",
            ["nflogRadar"] = "Predatory Perimeter Radar",
            ["processKill"] = "Forceful Peck",
            ["processTerm"] = "Warning Hiss",
            ["statHardware"] = "Gander Wing Anatomy",
            ["statTwampLatency"] = "Headwind / Tailwind RTT",
            ["asymmetry"] = "Formation Tilt",
            ["baremetalBadge"] = "Gander (Anchor)",
            ["vmBadge"] = "Gosling (VM)",
            ["navSectionViews"] = "Flight Decks",
            ["navSectionNodes"] = "The Gaggle Roster",
            ["navFleetMatrix"] = "The Pond",
            ["navGlobalRadar"] = "Migratory Flight Radar",
            ["radarTitle"] = "Migratory Flight Radar 🪿",
            ["radarSubtitle"] = "Worldwide migratory honk vectors, intercontinental thermal streams, and predatory drop tracking.",
            ["radarBadge"] = "Intercontinental Migration"
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

