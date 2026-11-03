using Microsoft.Win32;
using System.Windows.Threading;

namespace DAMeter.Platform;

/// <summary>Dispatches monitor changes back onto the window's UI thread.</summary>
public sealed class DisplayMonitorWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _changed;
    private bool _disposed;

    public DisplayMonitorWatcher(Dispatcher dispatcher, Action changed)
    {
        _dispatcher = dispatcher;
        _changed = changed;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs args)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                    _changed();
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
    }
}
