using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using DAMeter.Audio;
using DAMeter.Core;

namespace DAMeter.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsStore store;
    private readonly LoopbackAudioService audio;
    private readonly Dispatcher dispatcher;
    private readonly MeterBallistics ballistics = new();
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer;
    private double lastTime;
    private bool refreshing;
    private bool disposed;
    private AudioDeviceInfo? selectedDevice;
    private string status = "音声デバイスを準備しています…";

    public MainViewModel(SettingsStore store, AppSettings settings, Dispatcher dispatcher)
    {
        this.store = store;
        Settings = settings;
        this.dispatcher = dispatcher;
        audio = new LoopbackAudioService(store.Log);
        audio.StatusChanged += OnAudioStatus;
        timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        timer.Tick += Tick;
        SelectMeterCommand = new RelayCommand(p => { if (Enum.TryParse<MeterMode>(p?.ToString(), out var mode)) Meter = mode; });
        RefreshDevicesCommand = new RelayCommand(_ => RefreshDevices());
        ToggleFloatingCommand = new RelayCommand(_ => Floating = !Floating);
        ToggleClickThroughCommand = new RelayCommand(_ => { if (Floating) ClickThrough = !ClickThrough; });
        SettingsCommand = new RelayCommand(_ => OpenSettingsRequested?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<MeterBallistics, MeterMode>? FrameReady;
    public event Action? OpenSettingsRequested;
    public AppSettings Settings { get; }
    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];
    public ICommand SelectMeterCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand ToggleFloatingCommand { get; }
    public ICommand ToggleClickThroughCommand { get; }
    public ICommand SettingsCommand { get; }
    public string Status { get => status; private set { status = value; Changed(); } }
    public AudioDeviceInfo? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            selectedDevice = value;
            Changed();
            if (!refreshing && value is not null)
            {
                Settings.DeviceId = string.IsNullOrEmpty(value.Id) ? null : value.Id;
                audio.SelectDevice(Settings.DeviceId);
            }
        }
    }
    public MeterMode Meter { get => Settings.Meter; set { Settings.Meter = value; Changed(); } }
    public bool Floating { get => Settings.Floating; set { if (Settings.Floating == value) return; Settings.Floating = value; Changed(); Changed(nameof(Normal)); Changed(nameof(BackgroundOpacity)); } }
    public bool Normal => !Floating;
    public bool Topmost { get => Settings.Topmost; set { Settings.Topmost = value; Changed(); } }
    public bool ClickThrough { get => Settings.ClickThrough; set { Settings.ClickThrough = value; Changed(); } }
    public double BackgroundOpacity { get => Floating ? Settings.BackgroundOpacity : 1; set { Settings.BackgroundOpacity = Math.Clamp(value, 0, 1); Changed(); } }
    public double FloatingBackgroundOpacity { get => Settings.BackgroundOpacity; set { Settings.BackgroundOpacity = Math.Clamp(value, 0, 1); Changed(); Changed(nameof(BackgroundOpacity)); } }
    public double PeakHoldSeconds { get => Settings.PeakHoldSeconds; set { Settings.PeakHoldSeconds = Math.Clamp(value, 0, 30); Changed(); } }
    public double DecayDbPerSecond { get => Settings.DecayDbPerSecond; set { Settings.DecayDbPerSecond = Math.Clamp(value, 1, 120); Changed(); } }
    public double VuReferenceDb { get => Settings.VuReferenceDb; set { Settings.VuReferenceDb = Math.Clamp(value, -60, 0); Changed(); } }

    public void Start()
    {
        RefreshDevices();
        audio.SelectDevice(Settings.DeviceId);
        audio.Start();
        clock.Start();
        lastTime = 0;
        timer.Start();
    }

    public void RefreshDevices()
    {
        try
        {
            refreshing = true;
            Devices.Clear();
            Devices.Add(new AudioDeviceInfo("", "既定の再生デバイス（自動追従）"));
            foreach (var device in audio.GetDevices()) Devices.Add(device);
            var savedDevice = Devices.FirstOrDefault(d => d.Id == Settings.DeviceId);
            if (savedDevice is null && Settings.DeviceId is not null)
            {
                // Keep the unavailable manual choice visible so choosing the
                // default item really clears the pin rather than being a no-op.
                savedDevice = new AudioDeviceInfo(Settings.DeviceId, "選択した出力（未接続・復旧待ち）");
                Devices.Add(savedDevice);
            }
            SelectedDevice = savedDevice ?? Devices[0];
        }
        catch (Exception ex) { store.Log(ex); Status = "デバイス一覧を取得できません。自動復旧を待っています。"; }
        finally { refreshing = false; }
    }

    public void Save() => store.Save(Settings);

    private void Tick(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        ballistics.Update(audio.DrainLevels(), now - lastTime, PeakHoldSeconds, DecayDbPerSecond, VuReferenceDb);
        lastTime = now;
        FrameReady?.Invoke(ballistics, Meter);
    }

    private void OnAudioStatus(string message)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => { if (!disposed) Status = message; });
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Tick;
        audio.StatusChanged -= OnAudioStatus;
        audio.Dispose();
    }
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
