using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MadTOM.ViewModels;

public partial class MetricGraphGroupViewModel : ViewModelBase
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty] private string _title = string.Empty;

    public ObservableCollection<MetricGraphViewModel> Graphs { get; } = new();

    public bool HasMultipleGraphs => Graphs.Count > 1;
    public int GraphCount => Graphs.Count;

    public MetricGraphGroupViewModel(string title = "")
    {
        Title = title;
        Graphs.CollectionChanged += OnGraphsCollectionChanged;
    }

    public MetricGraphGroupViewModel(IEnumerable<MetricGraphViewModel> graphs, string title = "")
    {
        Title = title;
        foreach (var g in graphs) Graphs.Add(g);
        Graphs.CollectionChanged += OnGraphsCollectionChanged;
    }

    public MetricGraphGroupViewModel(MetricGraphViewModel singleGraph)
    {
        Title = singleGraph.Title;
        Graphs.Add(singleGraph);
        Graphs.CollectionChanged += OnGraphsCollectionChanged;
    }

    private void OnGraphsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMultipleGraphs));
        OnPropertyChanged(nameof(GraphCount));
    }
}

