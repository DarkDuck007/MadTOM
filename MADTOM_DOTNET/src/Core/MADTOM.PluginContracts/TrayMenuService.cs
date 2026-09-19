using System;
using System.Collections.Generic;
using System.Linq;

namespace MADTOM.PluginContracts;

/// <summary>
/// Standard implementation of <see cref="ITrayMenuService"/>.
/// Aggregates dynamically registered tray menu sections from loaded plugins.
/// </summary>
public class TrayMenuService : ITrayMenuService
{
    private readonly object _lock = new();
    private readonly List<ITrayMenuSection> _sections = new();

    public event EventHandler? SectionsChanged;

    public IDisposable RegisterSection(ITrayMenuSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        lock (_lock)
        {
            _sections.Add(section);
            section.ItemsChanged += OnSectionItemsChanged;
        }

        NotifySectionsChanged();

        return new SectionRegistrationToken(this, section);
    }

    public IReadOnlyList<ITrayMenuSection> GetSections()
    {
        lock (_lock)
        {
            return _sections.OrderBy(s => s.OrderWeight).ToList();
        }
    }

    private void UnregisterSection(ITrayMenuSection section)
    {
        lock (_lock)
        {
            if (_sections.Remove(section))
            {
                section.ItemsChanged -= OnSectionItemsChanged;
            }
        }

        NotifySectionsChanged();
    }

    private void OnSectionItemsChanged(object? sender, EventArgs e)
    {
        NotifySectionsChanged();
    }

    private void NotifySectionsChanged()
    {
        SectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SectionRegistrationToken : IDisposable
    {
        private readonly TrayMenuService _service;
        private ITrayMenuSection? _section;

        public SectionRegistrationToken(TrayMenuService service, ITrayMenuSection section)
        {
            _service = service;
            _section = section;
        }

        public void Dispose()
        {
            var section = System.Threading.Interlocked.Exchange(ref _section, null);
            if (section != null)
            {
                _service.UnregisterSection(section);
            }
        }
    }
}

