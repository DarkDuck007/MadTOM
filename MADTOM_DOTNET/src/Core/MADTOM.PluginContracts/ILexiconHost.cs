using System;
using System.Collections.Generic;

namespace MADTOM.PluginContracts;

/// <summary>
/// Host service allowing plugins to contribute localized lexicon dictionaries
/// and listen to global culture/language changes.
/// </summary>
public interface ILexiconHost
{
    /// <summary>
    /// The currently active global culture / lexicon key (e.g. "standard", "feline", "goose").
    /// </summary>
    string CurrentLexicon { get; }

    /// <summary>
    /// Event fired when the user switches the active lexicon / culture globally in the Console.
    /// </summary>
    event EventHandler<string>? CurrentLexiconChanged;

    /// <summary>
    /// Registers a dictionary of localized strings for a specific plugin and lexicon key.
    /// </summary>
    void RegisterLexicon(string pluginId, string lexiconKey, IReadOnlyDictionary<string, string> entries);

    /// <summary>
    /// Gets a localized string for a given key, searching the plugin's registered dictionary,
    /// falling back to the default culture or the raw key.
    /// </summary>
    string GetString(string pluginId, string key, string? fallback = null);
}

