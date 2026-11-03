using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace DAMeter.Mac.Platform;

/// <summary>Native macOS mouse passthrough and screen-safe window placement.</summary>
public static class MacWindowInterop
{
    /// <summary>
    /// Sets NSWindow.ignoresMouseEvents, which passes input through to other apps.
    /// A visible menu-bar recovery action must be registered before enabling this.
    /// </summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(window);
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("クリック透過は macOS の NSWindow にのみ適用できます。");

        // Avalonia.Native 11.3 exposes the NSWindow itself for desktop Windows,
        // not its IAvnWindow wrapper or content NSView. Never send an Objective-C
        // message to a handle from a different backend/type.
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0 || handle.HandleDescriptor != "NSWindow")
            throw new NotSupportedException("Avalonia から有効な NSWindow を取得できません。クリック透過は有効化されていません。");

        nint setter = ObjC.Selector("setIgnoresMouseEvents:");
        if (ObjC.RespondsToSelector(handle.Handle, ObjC.Selector("respondsToSelector:"), setter) == 0)
            throw new NotSupportedException("この NSWindow はクリック透過をサポートしていません。");

        ObjC.SetBoolean(handle.Handle, setter, enabled ? (byte)1 : (byte)0);
        bool applied = ObjC.GetBoolean(handle.Handle, ObjC.Selector("ignoresMouseEvents")) != 0;
        if (applied != enabled)
            throw new InvalidOperationException("macOS にクリック透過の変更を適用できませんでした。");
    }

    /// <summary>
    /// Fits the entire window into the nearest connected working area. Position
    /// and Screen.WorkingArea share Avalonia desktop coordinates. On macOS these
    /// are Cocoa points and Screen.Scaling is 1 even on Retina: RenderScaling is
    /// the framebuffer scale and must not be used for desktop placement.
    /// </summary>
    public static void EnsureVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Dispatcher.UIThread.VerifyAccess();
        if (window.WindowState != WindowState.Normal)
            return;

        var screens = window.Screens.All.Where(s => s.WorkingArea.Width > 0 && s.WorkingArea.Height > 0).ToArray();
        if (screens.Length == 0)
            return;

        double width = PositiveFinite(window.Bounds.Width, PositiveFinite(window.Width, 960));
        double height = PositiveFinite(window.Bounds.Height, PositiveFinite(window.Height, 640));
        double currentScale = PositiveFinite(window.Screens.ScreenFromWindow(window)?.Scaling ?? 1, 1);
        var frame = window.FrameSize ?? new Size(width, height);
        double frameExtraWidth = Math.Max(0, frame.Width - width);
        double frameExtraHeight = Math.Max(0, frame.Height - height);
        double frameWidth = (width + frameExtraWidth) * currentScale;
        double frameHeight = (height + frameExtraHeight) * currentScale;
        double centerX = window.Position.X + frameWidth / 2;
        double centerY = window.Position.Y + frameHeight / 2;

        // Favor overlap first, otherwise the nearest work area. This preserves a
        // chosen secondary monitor and valid negative coordinates when possible.
        Screen screen = screens.OrderByDescending(s => IntersectionArea(
                window.Position.X, window.Position.Y, frameWidth, frameHeight, s.WorkingArea))
            .ThenBy(s => DistanceSquared(centerX, centerY, s.WorkingArea)).First();
        var work = screen.WorkingArea;
        double scale = PositiveFinite(screen.Scaling, 1);
        double fitWidth = Math.Max(1, Math.Min(width, work.Width / scale - frameExtraWidth));
        double fitHeight = Math.Max(1, Math.Min(height, work.Height / scale - frameExtraHeight));
        double outerWidth = Math.Min(work.Width, (fitWidth + frameExtraWidth) * scale);
        double outerHeight = Math.Min(work.Height, (fitHeight + frameExtraHeight) * scale);
        int left = (int)Math.Clamp(window.Position.X, work.X, work.Right - outerWidth);
        int top = (int)Math.Clamp(window.Position.Y, work.Y, work.Bottom - outerHeight);

        if (Math.Abs(width - fitWidth) > 0.5) window.Width = fitWidth;
        if (Math.Abs(height - fitHeight) > 0.5) window.Height = fitHeight;
        if (window.Position.X != left || window.Position.Y != top)
            window.Position = new PixelPoint(left, top);
    }

    private static double PositiveFinite(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;

    private static double IntersectionArea(double x, double y, double width, double height, PixelRect work) =>
        Math.Max(0, Math.Min(x + width, work.Right) - Math.Max(x, work.X)) *
        Math.Max(0, Math.Min(y + height, work.Bottom) - Math.Max(y, work.Y));

    private static double DistanceSquared(double x, double y, PixelRect work)
    {
        double dx = x - Math.Clamp(x, work.X, work.Right);
        double dy = y - Math.Clamp(y, work.Y, work.Bottom);
        return dx * dx + dy * dy;
    }

    private static class ObjC
    {
        private const string Library = "/usr/lib/libobjc.A.dylib";

        [DllImport(Library, EntryPoint = "sel_registerName")]
        public static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern byte RespondsToSelector(nint target, nint selector, nint candidate);

        // Objective-C BOOL occupies one byte on both Intel and Apple Silicon.
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern void SetBoolean(nint target, nint selector, byte value);

        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern byte GetBoolean(nint target, nint selector);
    }
}
