using System;
using System.Collections.Generic;
using SQUEEZE.Models;

namespace SQUEEZE.Services;

public interface IPresetService
{
    IReadOnlyList<TranscodePreset> GetAllPresets();
    IReadOnlyList<TranscodePreset> GetPresetsByCategory(string category);
    TranscodePreset? GetPresetByKey(string key);
    string DefaultPresetKey { get; }
    void SetDefaultPresetKey(string key);
    TranscodePreset AddCustomPreset(string title, string description, TranscodePreset currentSettings);
    void LoadServerPresets(IEnumerable<TranscodePreset> serverPresets);
    event EventHandler<string>? DefaultPresetChanged;
    event EventHandler<TranscodePreset>? CustomPresetAdded;
    event EventHandler? PresetsReloaded;
}

