using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MadTOM.ViewModels;
using MadTOM.Views.Fleet;
using MadTOM.Views.HostDetail;
using MadTOM.Views.Radar;

namespace MadTOM;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // Direct strongly-typed mappings (fast, trim-safe, zero reflection failure)
        return param switch
        {
            FleetViewModel vm => new FleetView { DataContext = vm },
            HostDetailViewModel vm => new HostDetailView { DataContext = vm },
            GlobalRadarViewModel vm => new GlobalRadarView { DataContext = vm },
            _ => BuildViaReflection(param)
        };
    }

    private static Control? BuildViaReflection(object param)
    {
        var vmType = param.GetType();
        var vmFullName = vmType.FullName!;
        var candidateName = vmFullName.Replace("ViewModel", "View", StringComparison.Ordinal);

        var assembly = typeof(ViewLocator).Assembly;
        var type = assembly.GetType(candidateName);
        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        string typeName = vmType.Name.Replace("ViewModel", "View", StringComparison.Ordinal);
        string[] searchNamespaces = [
            $"MadTOM.Views.{typeName}",
            $"MadTOM.Views.Fleet.{typeName}",
            $"MadTOM.Views.HostDetail.{typeName}",
            $"MadTOM.Views.Radar.{typeName}"
        ];

        foreach (var fullCandidate in searchNamespaces)
        {
            type = assembly.GetType(fullCandidate);
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
