using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core;

public enum ReportedFeatureState { NotChecked, Enabled, Disabled }
public sealed record FeatureReport(ReportedFeatureState State, DateTimeOffset ReportedAt, string Fingerprint);
public sealed record TrackedGame
{
    public string Name { get; init; } = "";
    public string Root { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Source { get; init; } = "Manual";
    public SetupRoute? Route { get; init; }
    public GraphicsApi? GraphicsApi { get; init; }
    public GameSnapshot? Snapshot { get; init; }
    public DateTimeOffset? InstalledAt { get; init; }
    public Dictionary<string, FeatureReport> FeatureReports { get; init; } = [];
    public string Status => Snapshot == null ? "Not checked" : !Snapshot.Complete ? "Check needed" :
        FeatureReports.Any(r => r.Value.State == ReportedFeatureState.Enabled && r.Value.Fingerprint == Snapshot.Fingerprint)
            ? "Enabled · reported" : Snapshot.Has("nvngx_dlssnr.dll") ? "Neural Rendering installed" :
        Snapshot.Has("nvngx_dlss.dll") ? "DLSS installed" : "No DLSS files found";
}
public sealed class LibraryDocument
{
    public int Schema { get; set; } = 1;
    public List<TrackedGame> Games { get; set; } = [];
}
public sealed record FeatureStatus(string Key, string Name, string Component, string InGame, string Evidence);
public static class GameFeatures
{
    public static readonly (string Key, string Name, string File)[] Definitions =
        [("sr", "Super Resolution", "nvngx_dlss.dll"), ("rr", "Ray Reconstruction", "nvngx_dlssd.dll"),
         ("fg", "Frame Generation", "nvngx_dlssg.dll"), ("nr", "Neural Rendering", "nvngx_dlssnr.dll")];
    public static FeatureStatus[] Describe(TrackedGame game) => Definitions.Select(feature =>
    {
        var report = game.FeatureReports.GetValueOrDefault(feature.Key);
        bool current = report != null && game.Snapshot?.Complete == true && report.Fingerprint == game.Snapshot.Fingerprint;
        string inGame = report == null || report.State == ReportedFeatureState.NotChecked ? "Not checked" : !current ? "Recheck needed" :
            report.State == ReportedFeatureState.Enabled ? "Enabled · reported" : "Disabled · reported";
        return new FeatureStatus(feature.Key, feature.Name,
            game.Snapshot?.Has(feature.File) == true ? "Installed" : game.Snapshot?.Complete == true ? "Not found" : "Unknown", inGame,
            current && report!.State != ReportedFeatureState.NotChecked ? $"Your report · {report.ReportedAt:g}" : "Check in the game's settings or add-on panel");
    }).ToArray();
}

// Local inventory only. No game settings are changed by this store.
public sealed class GameLibraryStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS-Nexus", "library.json");
    private readonly string path;
    private readonly object gate = new();
    public GameLibraryStore(string? filePath = null) => path = Path.GetFullPath(filePath ?? DefaultPath);
    public IReadOnlyList<TrackedGame> Read()
    {
        lock (gate)
        {
            FileSafety.EnsureNoLinks(path);
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Game library is too large.");
            var document = JsonSerializer.Deserialize<LibraryDocument>(File.ReadAllText(path), JsonSettings.Options)
                ?? throw new InvalidDataException("Game library could not be read.");
            if (document.Schema != 1 || document.Games == null || document.Games.Count > 1000)
                throw new InvalidDataException("Game library format is unsupported.");
            foreach (var game in document.Games)
            {
                if (game == null || string.IsNullOrWhiteSpace(game.Name) || !Path.IsPathFullyQualified(game.Root) ||
                    !Path.IsPathFullyQualified(game.Executable) || !FileSafety.IsInside(game.Root, game.Executable) || game.FeatureReports == null)
                    throw new InvalidDataException("Game library contains an invalid entry.");
                if (game.Snapshot is { } snapshot && (snapshot.Components == null || snapshot.Notices == null ||
                    snapshot.Components.Count > 1000 || snapshot.Components.Any(c => c == null || string.IsNullOrEmpty(c.Hash) ||
                    !Path.IsPathFullyQualified(c.Path) || !FileSafety.IsInside(game.Root, c.Path))))
                    throw new InvalidDataException("Game library contains an invalid snapshot.");
                if (game.GraphicsApi is { } api && !Enum.IsDefined(api))
                    throw new InvalidDataException("Game library contains an invalid graphics API.");
                if (game.FeatureReports.Any(r => !GameFeatures.Definitions.Any(d => d.Key == r.Key) || r.Value == null || !Enum.IsDefined(r.Value.State)))
                    throw new InvalidDataException("Game library contains an invalid feature report.");
            }
            if (document.Games.Select(g => g.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Games.Count)
                throw new InvalidDataException("Game library contains duplicate entries.");
            return document.Games;
        }
    }
    public void Save(TrackedGame game)
    {
        lock (gate)
        {
            using var access = AcquireWriteLock();
            var games = Read().Where(g => !g.Executable.Equals(game.Executable, StringComparison.OrdinalIgnoreCase)).Append(game).ToList();
            if (games.Count > 1000 || string.IsNullOrWhiteSpace(game.Name) || !Path.IsPathFullyQualified(game.Root) || !Path.IsPathFullyQualified(game.Executable) ||
                !FileSafety.IsInside(game.Root, game.Executable)) throw new InvalidDataException("Invalid game entry.");
            FileSafety.EnsureNoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(new LibraryDocument { Games = games }, JsonSettings.Options);
                if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidDataException("Game library exceeds the size limit.");
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public TrackedGame Record(GameTarget target, string name, string source, GameSnapshot snapshot, SetupRoute? route = null, bool installed = false, GraphicsApi? graphicsApi = null)
    {
        lock (gate)
        {
            using var access = AcquireWriteLock();
            var previous = Read().FirstOrDefault(g => g.Executable.Equals(target.Executable, StringComparison.OrdinalIgnoreCase));
            var game = new TrackedGame
            {
                Name = name, Root = target.Root, Executable = target.Executable, Source = source,
                Snapshot = snapshot, Route = route ?? previous?.Route,
                GraphicsApi = graphicsApi ?? previous?.GraphicsApi,
                InstalledAt = installed ? DateTimeOffset.UtcNow : previous?.InstalledAt,
                FeatureReports = previous?.FeatureReports ?? []
            };
            Save(game); return game;
        }
    }
    public TrackedGame ReportFeature(string executable, string key, ReportedFeatureState state)
    {
        lock (gate)
        {
            using var access = AcquireWriteLock();
            if (!GameFeatures.Definitions.Any(d => d.Key == key) || !Enum.IsDefined(state)) throw new ArgumentException("Unknown feature or state.");
            var game = Read().Single(g => g.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase));
            if (game.Snapshot?.Complete != true) throw new InvalidOperationException("Refresh the game before saving feature settings.");
            var reports = new Dictionary<string, FeatureReport>(game.FeatureReports)
                { [key] = new(state, DateTimeOffset.UtcNow, game.Snapshot.Fingerprint) };
            game = game with { FeatureReports = reports }; Save(game); return game;
        }
    }
    private IDisposable AcquireWriteLock()
    {
        var mutex = new Mutex(false, "DLSS-Nexus-Library-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))));
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Another copy of Nexus is updating the library. Try again."); }
            catch (AbandonedMutexException) { /* Ownership transfers to this thread. */ }
            return new LibraryLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class LibraryLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
