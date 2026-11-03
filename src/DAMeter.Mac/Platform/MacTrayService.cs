using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace DAMeter.Mac.Platform;

/// <summary>Native macOS menu-bar controls, including a mouse-passthrough escape.</summary>
public sealed class MacTrayService : IDisposable
{
    private TrayIcon? icon;
    private bool disposed;

    public MacTrayService(
        Application app,
        Action show,
        Action hide,
        Action normal,
        Action floating,
        Action toggleClickThrough,
        Action toggleTopmost,
        Action settings,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(app);
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsMacOS())
            return;

        var menu = new NativeMenu();
        Add(menu, "ウィンドウを表示（クリック透過を解除）", show);
        Add(menu, "ウィンドウを非表示", hide);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, "通常表示に戻す", normal);
        Add(menu, "フローティング表示にする", floating);
        Add(menu, "クリック透過 ON / OFF", toggleClickThrough);
        Add(menu, "常に最前面 ON / OFF", toggleTopmost);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, "設定…", settings);
        Add(menu, "DAMeter を終了", exit);

        using var image = AssetLoader.Open(new Uri("avares://DAMeter.Mac/Assets/dameter.png"));
        icon = new TrayIcon
        {
            Icon = new WindowIcon(image),
            ToolTipText = "DAMeter — システム音声メーター",
            Menu = menu,
            IsVisible = true
        };

        // TrayIcon has no public IsSupported property in Avalonia 11.3. The
        // native exporter is present only when the backend supplied a tray impl.
        // This is a capability check, not a claim of a tested visible icon.
        if (icon.NativeMenuExporter is null)
        {
            icon.Dispose();
            icon = null;
        }

        // macOS intentionally does not raise TrayIcon.Clicked: its menu opens
        // on click. Recovery is the first menu action, not a double-click hook.
        // Hold the icon for this service's lifetime; App disposes this service
        // when the classic desktop lifetime exits.
    }

    public bool IsAvailable => !disposed && icon is { IsVisible: true, NativeMenuExporter: not null };

    private void Add(NativeMenu menu, string header, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!disposed) action();
        });
        menu.Items.Add(item);
    }

    public void Dispose()
    {
        if (disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        disposed = true;
        if (icon is not null)
        {
            icon.IsVisible = false;
            icon.Dispose();
            icon = null;
        }
    }
}
