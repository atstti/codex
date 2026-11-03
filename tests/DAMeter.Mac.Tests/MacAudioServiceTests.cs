using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DAMeter.Core;
using DAMeter.Mac.Audio;
using Xunit;

namespace DAMeter.Mac.Tests;

/// <summary>
/// Exercises real child-process pipes with a test-only helper, without accessing
/// macOS capture permissions or devices. No test audio is written to a file.
/// </summary>
public sealed class MacAudioServiceTests
{
    [UnixAudioFact]
    public async Task AgentProbeUsesItsOwnIdentityInsteadOfFinderParentIdentity()
    {
        using var helper = new TestHelper("""
if sys.argv[1:] != ["--agent-check"]:
    sys.exit(78)
for key in ["__CFBundleIdentifier", "__CFBundleIdentifierOverride", "CFProcessPath"]:
    if key in os.environ:
        sys.stderr.write("ERROR: Inherited parent identity\n")
        sys.exit(78)
sys.stderr.write("STATUS: bundle=org.atstti.DAMeter.AudioCapture activationPolicy=accessory\n")
sys.stderr.flush()
""");
        string[] keys = ["__CFBundleIdentifier", "__CFBundleIdentifierOverride", "CFProcessPath"];
        var previous = keys.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, "org.atstti.DAMeter");
            using var service = new MacLoopbackAudioService(helperPath: helper.Path);
            await service.VerifyAgentAsync();
            Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        }
        finally
        {
            for (int index = 0; index < keys.Length; index++) Environment.SetEnvironmentVariable(keys[index], previous[index]);
        }
    }

    [UnixAudioFact]
    public async Task AgentProbeRejectsASecondRegularApplicationIdentity()
    {
        using var helper = new TestHelper("""
sys.stderr.write("STATUS: bundle=org.atstti.DAMeter activationPolicy=regular\n")
sys.stderr.flush()
""");
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAgentAsync());
    }

    [UnixAudioFact]
    public async Task ReadsStereoPcmAndExpiresMeasurementsWhenPacketsStop()
    {
        using var helper = new TestHelper("""
event("start")
for index in range(40):
    send_packet()
    time.sleep(0.025)
sys.stdin.readline()
event("stop")
""");
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        service.Start();
        AudioLevels measured = default;
        await WaitUntilAsync(() =>
        {
            measured = service.DrainLevels();
            return measured.LeftPeak == 1;
        });

        Assert.Equal(0.75, measured.RightPeak);
        Assert.Equal(Math.Sqrt(0.625), measured.LeftRms, 8);
        Assert.Equal(Math.Sqrt(0.3125), measured.RightRms, 8);
        await WaitUntilAsync(() => service.DrainLevels() == AudioLevels.Silence);
    }

    [UnixAudioFact]
    public async Task StartIsIdempotentAndRetryReconnectsBeforeGracefulStopAndRestart()
    {
        using var helper = new TestHelper("""
event("start")
stream_until_stopped()
event("stop")
""");
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        service.Start();
        service.Start();
        service.Start();
        await WaitUntilAsync(() => service.DrainLevels().LeftPeak == 1);
        Assert.Equal(1, helper.CountEvents("start"));

        service.Retry();
        await WaitUntilAsync(() => helper.CountEvents("start") == 2 && service.DrainLevels().LeftPeak == 1);
        service.Stop();
        Assert.Equal(2, helper.CountEvents("stop"));
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());

        service.Start();
        await WaitUntilAsync(() => helper.CountEvents("start") == 3 && service.DrainLevels().LeftPeak == 1);
        service.Stop();
        Assert.Equal(3, helper.CountEvents("stop"));
    }

    [UnixAudioFact]
    public async Task PermissionDeniedPausesAutomaticRestartUntilExplicitRetry()
    {
        using var helper = new TestHelper("""
event("start")
sys.stderr.write("ERROR: Permission denied\n")
sys.stderr.flush()
sys.exit(77)
""");
        var statuses = new ConcurrentQueue<string>();
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        service.StatusChanged += statuses.Enqueue;
        service.Start();
        await WaitUntilAsync(() => statuses.Any(message => message.Contains("画面・システム音声", StringComparison.Ordinal)));

        // Ordinary stream errors retry after two seconds. Permission refusal must
        // remain paused beyond that interval so it cannot repeatedly prompt.
        await Task.Delay(TimeSpan.FromMilliseconds(2300));
        Assert.Equal(1, helper.CountEvents("start"));
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        service.Retry();
        await WaitUntilAsync(() => helper.CountEvents("start") == 2);
    }

    [UnixAudioFact]
    public async Task InvalidAgentBundlePausesAutomaticRestartUntilExplicitRetry()
    {
        using var helper = new TestHelper("""
event("start")
sys.stderr.write("ERROR: Invalid capture-agent bundle\n")
sys.stderr.flush()
sys.exit(78)
""");
        var statuses = new ConcurrentQueue<string>();
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        service.StatusChanged += statuses.Enqueue;
        service.Start();
        await WaitUntilAsync(() => statuses.Any(message => message.Contains("アプリ構成", StringComparison.Ordinal)));
        await Task.Delay(TimeSpan.FromMilliseconds(2300));
        Assert.Equal(1, helper.CountEvents("start"));
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        service.Retry();
        await WaitUntilAsync(() => helper.CountEvents("start") == 2);
    }

    [UnixAudioFact]
    public async Task MissingHelperCanBeInstalledAndStartedWithExplicitRetry()
    {
        using var helper = new TestHelper(body: null);
        var statuses = new ConcurrentQueue<string>();
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        service.StatusChanged += statuses.Enqueue;
        service.Start();
        await WaitUntilAsync(() => statuses.Any(message => message.Contains("見つかりません", StringComparison.Ordinal)));
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());

        helper.WriteScript("""
event("start")
stream_until_stopped()
event("stop")
""");
        service.Retry();
        await WaitUntilAsync(() => service.DrainLevels().LeftPeak == 1);
        Assert.Equal(1, helper.CountEvents("start"));
    }

    [UnixAudioFact]
    public async Task InvalidPacketIsLoggedAndCaptureRecoversWithAFreshProcess()
    {
        using var helper = new TestHelper("""
first_start = not os.path.exists(events_path)
event("start")
if first_start:
    sys.stdout.buffer.write(struct.pack("<I", 7))
    sys.stdout.buffer.flush()
    sys.stdin.readline()
else:
    stream_until_stopped()
event("stop")
""");
        var errors = new ConcurrentQueue<Exception>();
        using var service = new MacLoopbackAudioService(errors.Enqueue, helper.Path);
        service.Start();
        await WaitUntilAsync(() => errors.Any(exception => exception is InvalidDataException));
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        await WaitUntilAsync(() => helper.CountEvents("start") == 2 && service.DrainLevels().LeftPeak == 1);
        Assert.Contains(errors, exception => exception is InvalidDataException);
    }

    [UnixAudioFact]
    public async Task StopBeforeFirstAudioPacketTerminatesAnUnresponsivePermissionWait()
    {
        using var helper = new TestHelper("""
event(str(os.getpid()))
while True:
    time.sleep(0.05)
""");
        using var service = new MacLoopbackAudioService(helperPath: helper.Path);
        service.Start();
        await WaitUntilAsync(() => helper.Events.Length == 1);
        var processId = int.Parse(Assert.Single(helper.Events));
        var elapsed = Stopwatch.StartNew();
        service.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(6), "A helper waiting before its first packet blocked shutdown.");
        Assert.True(ProcessExited(processId));
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
    }

    [UnixAudioFact]
    public async Task DisposeTerminatesAHelperThatIgnoresGracefulStop()
    {
        using var helper = new TestHelper("""
event(str(os.getpid()))
while True:
    send_packet()
    time.sleep(0.025)
""");
        var service = new MacLoopbackAudioService(helperPath: helper.Path);
        try
        {
            service.Start();
            await WaitUntilAsync(() => service.DrainLevels().LeftPeak == 1);
            var processId = int.Parse(Assert.Single(helper.Events));
            var elapsed = Stopwatch.StartNew();
            service.Dispose();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Helper shutdown exceeded its grace period and kill allowance.");
            Assert.True(ProcessExited(processId), "The capture helper survived service disposal.");
            Assert.Equal(AudioLevels.Silence, service.DrainLevels());
            Assert.Throws<ObjectDisposedException>(() => service.Start());
            Assert.Throws<ObjectDisposedException>(() => service.Retry());
        }
        finally { service.Dispose(); }
    }

    private static bool ProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(12), "Timed out waiting for child-process transport state.");
            await Task.Delay(15);
        }
    }

    private sealed class TestHelper : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DAMeter-MacAudio-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "test-audio-helper");
        private string EventsPath => System.IO.Path.Combine(directory, "process-events.txt");
        public string[] Events => File.Exists(EventsPath) ? File.ReadAllLines(EventsPath) : [];
        public int CountEvents(string value) => Events.Count(line => line == value);

        public TestHelper(string? body)
        {
            Directory.CreateDirectory(directory);
            if (body is not null) WriteScript(body);
        }

        public void WriteScript(string body)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var script = $$"""
#!/usr/bin/env python3
import os, struct, sys, threading, time
events_path = {{JsonSerializer.Serialize(EventsPath)}}

def event(value):
    with open(events_path, "a", encoding="utf-8") as log:
        log.write(value + "\n")

def send_packet():
    payload = struct.pack("<ffff", 0.5, -0.25, -1.0, 0.75)
    sys.stdout.buffer.write(struct.pack("<I", len(payload)) + payload)
    sys.stdout.buffer.flush()

def stream_until_stopped():
    stopped = threading.Event()
    def read_stop():
        sys.stdin.readline()
        stopped.set()
    threading.Thread(target=read_stop, daemon=True).start()
    while not stopped.is_set():
        send_packet()
        time.sleep(0.025)

{{body}}
""";
            File.WriteAllText(Path, script + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}

public sealed class UnixAudioFactAttribute : FactAttribute
{
    public UnixAudioFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Subprocess transport tests use Unix executable helpers; run on Linux or macOS.";
    }
}
