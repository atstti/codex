using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DAMeter.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter<MeterMode>(allowIntegerValues: false) }
    };

    private readonly object sync = new();
    private readonly string directory;

    public SettingsStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DAMeter");
    }

    public AppSettings Load()
    {
        lock (sync)
        {
            try
            {
                string path = Path.Combine(directory, "settings.json");
                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                    ?? throw new JsonException("Settings must be a JSON object.");
                settings.Validate();
                return settings;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log(exception);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (sync)
        {
            settings.Validate();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.json");
            string temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                // Write beside the destination so the final rename stays on the same volume.
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                // Failed writes leave the previous settings untouched.
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Log(Exception exception)
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "exceptions.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                {
                    File.Move(path, Path.Combine(directory, "exceptions.previous.log"), overwrite: true);
                }

                File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] {exception}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // An unwritable log directory must not crash audio recovery or application startup.
            }
        }
    }
}
