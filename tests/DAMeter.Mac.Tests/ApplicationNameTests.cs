using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class ApplicationNameTests
{
    [AvaloniaFact]
    public void NameIsAvailableBeforeApplicationInitialization()
    {
        var current = Application.Current;
        var app = new App();
        Assert.Equal("DAMeter", app.Name);
        Assert.Same(current, Application.Current);
    }

    [AvaloniaFact]
    public void LoadedApplicationRetainsTheNameUsedByTheMacMenuExporter()
    {
        var app = Assert.IsType<App>(Application.Current);
        Assert.NotEmpty(app.Styles);
        Assert.Equal("DAMeter", app.Name);
    }
}
