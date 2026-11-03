using Avalonia;
namespace DAMeter.Mac;
internal static class Program
{
    public static bool SmokeTest { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        SmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
