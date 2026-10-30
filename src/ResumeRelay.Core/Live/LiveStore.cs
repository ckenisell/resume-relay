using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResumeRelay.Live;

/// <summary>
/// Live Poster's settings (live/live.json) and its folders: backgrounds, Coming Soon posters, found posters and the
/// rendered videos. Writes go through a temp file, like the main store.
/// </summary>
public sealed class LiveStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _lock = new();
    private LiveConfig _cfg;

    public string Dir { get; }
    public string BackgroundDir => Path.Combine(Dir, "backgrounds");
    public string ComingDir => Path.Combine(Dir, "coming");
    public string PosterDir => Path.Combine(Dir, "posters");
    public string RenderDir => Path.Combine(Dir, "renders");
    private string File_ => Path.Combine(Dir, "live.json");

    public event Action? Changed;

    public LiveStore(Store store)
    {
        Dir = Path.Combine(store.DataDirectory, "live");
        foreach (var d in new[] { Dir, BackgroundDir, ComingDir, PosterDir, RenderDir }) Directory.CreateDirectory(d);
        try { _cfg = File.Exists(File_) ? JsonSerializer.Deserialize<LiveConfig>(File.ReadAllText(File_), Json) ?? new() : new(); }
        catch { _cfg = new(); }
    }

    public LiveConfig Get()
    {
        lock (_lock) return Clone(_cfg);
    }

    /// <summary>Changes the settings in one step (read, change, save) so two changes never overwrite each other.</summary>
    public LiveConfig Update(Action<LiveConfig> change)
    {
        LiveConfig copy;
        lock (_lock)
        {
            change(_cfg);
            Save();
            copy = Clone(_cfg);
        }
        Changed?.Invoke();
        return copy;
    }

    private void Save()
    {
        string tmp = File_ + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_cfg, Json));
        File.Move(tmp, File_, true);
    }

    private static LiveConfig Clone(LiveConfig c) => JsonSerializer.Deserialize<LiveConfig>(JsonSerializer.Serialize(c, Json), Json)!;

    public string BackgroundPath(LiveBackground b) => Path.Combine(BackgroundDir, b.File);
    public string ComingPath(ComingSoonItem c) => Path.Combine(ComingDir, c.File);
}
