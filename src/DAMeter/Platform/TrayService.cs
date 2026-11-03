using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DAMeter.Platform;

/// <summary>Keeps the application reachable even while its window ignores mouse input.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public TrayService(
        Action show,
        Action hide,
        Action normal,
        Action floating,
        Action toggleClickThrough,
        Action toggleTopmost,
        Action settings,
        Action exit)
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _menu = new Forms.ContextMenuStrip();
        Add("ウィンドウを表示（クリック透過を解除）", show);
        Add("ウィンドウを非表示", hide);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add("通常モードに戻す", normal);
        Add("フローティングモードにする", floating);
        Add("クリック透過 ON / OFF", toggleClickThrough);
        Add("常に最前面 ON / OFF", toggleTopmost);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add("設定", settings);
        Add("アプリ終了", exit);

        _icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "DAMeter",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Invoke(show);
    }

    private void Add(string text, Action callback)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => Invoke(callback);
        _menu.Items.Add(item);
    }

    private void Invoke(Action callback)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            _dispatcher.InvokeAsync(callback);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
