using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace MadTOM.Localization;

public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LexiconService.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
