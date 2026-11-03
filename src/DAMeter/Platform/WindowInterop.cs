using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace DAMeter.Platform;

public static class WindowInterop
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExLayered = 0x00080000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Use a layered transparent window so mouse input reaches windows in other
    /// processes too. Returning HTTRANSPARENT alone would only cover this thread.
    /// </summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        long updated = enabled ? style | WsExTransparent | WsExLayered : style & ~WsExTransparent;
        if (updated == style)
            return;

        Marshal.SetLastPInvokeError(0);
        nint previous = SetWindowLongPtr(handle, GwlExStyle, new nint(updated));
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error);

        if (!SetWindowPos(handle, 0, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// Clamp the native window rectangle to a connected monitor's working area.
    /// Both rectangles use physical desktop pixels, avoiding incorrect conversion
    /// of negative coordinates or mixed-DPI monitor origins into WPF DIPs.
    /// </summary>
    public static void EnsureVisible(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (window.WindowState == WindowState.Minimized)
            return;

        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            EventHandler? initialized = null;
            initialized = (_, _) =>
            {
                window.SourceInitialized -= initialized;
                window.Dispatcher.InvokeAsync(() => EnsureVisible(window));
            };
            window.SourceInitialized += initialized;
            return;
        }

        if (!GetWindowRect(handle, out RectNative rect))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        var actual = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
        var work = Forms.Screen.FromRectangle(actual).WorkingArea;
        if (work.Width <= 0 || work.Height <= 0)
            return;

        int fitWidth = Math.Min(width, work.Width);
        int fitHeight = Math.Min(height, work.Height);
        int left = Math.Clamp(rect.Left, work.Left, work.Right - fitWidth);
        int top = Math.Clamp(rect.Top, work.Top, work.Bottom - fitHeight);

        if (left == rect.Left && top == rect.Top && fitWidth == width && fitHeight == height)
            return;

        if (!SetWindowPos(handle, 0, left, top, fitWidth, fitHeight, SwpNoZOrder | SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static nint GetWindowLongPtr(nint handle, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(handle, index) : GetWindowLong32(handle, index);

    private static nint SetWindowLongPtr(nint handle, int index, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(handle, index, value) : SetWindowLong32(handle, index, value.ToInt32());

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out RectNative rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
