using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace MadTOM.Localization;

public interface ILexiconService : INotifyPropertyChanged
{
    string CurrentPack { get; }
    IReadOnlyList<string> AvailablePacks { get; }
    string this[string key] { get; }
    void LoadLexicon(string packName);
    void ImportLexicon(string packName, string jsonOrFilePath);
    void RegisterLexicon(string packName, Dictionary<string, string> tokens);
    event EventHandler<string>? LexiconChanged;
}

