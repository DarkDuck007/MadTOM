using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MadTOM.ViewModels;

namespace MadTOM;

[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var vmType = param.GetType();
        var vmFullName = vmType.FullName!;
        var candidateName = vmFullName.Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(candidateName);
        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        // Check well-known subnamespaces if grouped into subfolders
        string typeName = vmType.Name.Replace("ViewModel", "View", StringComparison.Ordinal);
        string[] searchNamespaces = [
            $"MadTOM.Views.{typeName}",
            $"MadTOM.Views.Fleet.{typeName}",
            $"MadTOM.Views.HostDetail.{typeName}",
            $"MadTOM.Views.Radar.{typeName}"
        ];

        foreach (var fullCandidate in searchNamespaces)
        {
            type = Type.GetType(fullCandidate);
            if (type != null)
            {
                return (Control)Activator.CreateInstance(type)!;
            }
        }

        return new TextBlock { Text = "View Not Found: " + typeName };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
