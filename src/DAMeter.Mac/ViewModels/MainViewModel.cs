using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using DAMeter.Core;
using DAMeter.Mac.Audio;

namespace DAMeter.Mac.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsStore store;
    private readonly MacLoopbackAudioService audio;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1.0 / 60) };
    private readonly Stopwatch clock = new();
    private readonly MeterBallistics ballistics = new();
    private double lastTime;
    private bool disposed;
    private string status = "システム音声を準備しています…";
    public MainViewModel(SettingsStore store, AppSettings settings)
    {
        this.store = store;
        Settings = settings;
        audio = new MacLoopbackAudioService(store.Log);
        audio.StatusChanged += OnStatus;
        timer.Tick += Tick;
        SelectMeterCommand = new RelayCommand(p => { if (Enum.TryParse<MeterMode>(p?.ToString(), out var mode)) Meter = mode; });
        RetryCommand = new RelayCommand(_ => audio.Retry());
        ToggleFloatingCommand = new RelayCommand(_ => Floating = !Floating);
        SettingsCommand = new RelayCommand(_ => OpenSettingsRequested?.Invoke());
    }
    public AppSettings Settings { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<MeterBallistics, MeterMode>? FrameReady;
    public event Action? OpenSettingsRequested;
    public ICommand SelectMeterCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ToggleFloatingCommand { get; }
    public ICommand SettingsCommand { get; }
    public string Status { get => status; set { status = value; Changed(); } }
    public MeterMode Meter { get => Settings.Meter; set { Settings.Meter = value; Changed(); } }
    public bool Floating { get => Settings.Floating; set { if (Settings.Floating == value) return; Settings.Floating = value; Changed(); Changed(nameof(Normal)); Changed(nameof(BackgroundOpacity)); } }
    public bool Normal => !Floating;
    public bool Topmost { get => Settings.Topmost; set { Settings.Topmost = value; Changed(); } }
    public bool ClickThrough { get => Settings.ClickThrough; set { Settings.ClickThrough = value; Changed(); } }
    public double BackgroundOpacity => Floating ? Settings.BackgroundOpacity : 1;
    public double FloatingBackgroundOpacity { get => Settings.BackgroundOpacity; set { Settings.BackgroundOpacity = Math.Clamp(value, 0, 1); Changed(); Changed(nameof(BackgroundOpacity)); } }
    public double PeakHoldSeconds { get => Settings.PeakHoldSeconds; set { Settings.PeakHoldSeconds = Math.Clamp(value, 0, 30); Changed(); } }
    public double DecayDbPerSecond { get => Settings.DecayDbPerSecond; set { Settings.DecayDbPerSecond = Math.Clamp(value, 1, 120); Changed(); } }
    public double VuReferenceDb { get => Settings.VuReferenceDb; set { Settings.VuReferenceDb = Math.Clamp(value, -60, 0); Changed(); } }
    public void Start()
    {
        audio.Start();
        clock.Restart();
        lastTime = 0;
        timer.Start();
    }
    private void Tick(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        ballistics.Update(audio.DrainLevels(), now - lastTime, PeakHoldSeconds, DecayDbPerSecond, VuReferenceDb);
        lastTime = now;
        FrameReady?.Invoke(ballistics, Meter);
    }
    private void OnStatus(string message) => Dispatcher.UIThread.Post(() => { if (!disposed) Status = message; });
    public void Save() => store.Save(Settings);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop(); timer.Tick -= Tick;
        audio.StatusChanged -= OnStatus;
        audio.Dispose();
    }
}
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
