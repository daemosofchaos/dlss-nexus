using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Core;

public sealed record ReShadeRelease(string Version, string DownloadUrl);
public sealed record ReShadePackage(string Version, string ModulePath, string ModuleHash,
    string SetupPath, string SetupHash, string DownloadUrl);
public sealed record ReShadeAcquisition(ReShadePackage Package, string Status);
public sealed record ReShadeUpdate(ReShadeRelease? Release, string Status);

public sealed partial class ReShadeService
{
    public static readonly Version MinimumVersion = new(6, 8, 0);
    public static string DefaultCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS-Nexus", "Cache", "ReShade");
    private readonly string cache;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    public ReShadeService(string? cacheDirectory = null, HttpClient? httpClient = null)
    {
        cache = Path.GetFullPath(cacheDirectory ?? DefaultCache);
        client = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    }
    [GeneratedRegex(@"(?:https://reshade\.me)?/downloads/ReShade_Setup_(\d{1,4}\.\d{1,4}\.\d{1,4})_Addon\.exe", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadLink();
    public static ReShadeRelease ParseRelease(string html)
    {
        var matches = DownloadLink().Matches(html).Select(m => new ReShadeRelease(m.Groups[1].Value, "https://reshade.me/downloads/ReShade_Setup_" + m.Groups[1].Value + "_Addon.exe"));
        return matches.Where(r => Version.Parse(r.Version) >= MinimumVersion).OrderByDescending(r => Version.Parse(r.Version)).FirstOrDefault()
            ?? throw new InvalidDataException("No supported full add-on ReShade download was found on the official site.");
    }
    public static void ValidateRelease(ReShadeRelease release)
    {
        if (!Version.TryParse(release.Version, out var version) || version < MinimumVersion || version.ToString(3) != release.Version ||
            release.DownloadUrl != $"https://reshade.me/downloads/ReShade_Setup_{release.Version}_Addon.exe")
            throw new InvalidDataException("Only official HTTPS full add-on ReShade downloads are supported.");
    }
    private async Task<byte[]> DownloadAsync(string url, int maximumBytes, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != url) throw new InvalidDataException("Unexpected download redirect; the official address must be used.");
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Official download exceeds the size limit.");
        using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream(); byte[] buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Download exceeds the size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public async Task<ReShadeUpdate> CheckLatestAsync(CancellationToken cancellation = default)
    {
        try
        {
            byte[] page = await DownloadAsync("https://reshade.me/", 2 * 1024 * 1024, cancellation);
            var release = ParseRelease(System.Text.Encoding.UTF8.GetString(page));
            return new(release, $"Official full add-on release: {release.Version} · checked {DateTime.Now:t}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new(null, "Update check unavailable. A verified cached download can still be used.");
        }
    }
    public async Task<ReShadeAcquisition> AcquireAsync(IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            FileSafety.EnsureNoLinks(cache); Directory.CreateDirectory(cache);
            progress?.Report("Checking the official ReShade add-on release…");
            var update = await CheckLatestAsync(cancellation);
            var cached = FindCached();
            if (update.Release == null)
                return cached == null ? throw new IOException("ReShade is missing and the official download is unavailable. Connect to the internet and preview again.")
                    : new(cached, $"Using verified cached ReShade {cached.Version}; update check unavailable.");
            ValidateRelease(update.Release);
            if (cached != null && Version.Parse(cached.Version) >= Version.Parse(update.Release.Version))
                return new(cached, $"Using verified cached ReShade {cached.Version} · official update check complete.");
            string? pending = null;
            try
            {
                progress?.Report($"Downloading official ReShade {update.Release.Version} with full add-on support…");
                byte[] setup = await DownloadAsync(update.Release.DownloadUrl, 64 * 1024 * 1024, cancellation);
                byte[] module = ExtractModule(setup, update.Release.Version);
                string versionRoot = FileSafety.SafePath(cache, update.Release.Version);
                FileSafety.EnsureNoLinks(versionRoot);
                // New cache records have unique immutable paths. Failed/corrupted records are never reused.
                pending = FileSafety.SafePath(versionRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending);
                string setupPath = Path.Combine(pending, "ReShade_Setup_Addon.exe"), modulePath = Path.Combine(pending, "ReShade64.dll");
                await File.WriteAllBytesAsync(setupPath, setup, cancellation); await File.WriteAllBytesAsync(modulePath, module, cancellation);
                var package = new ReShadePackage(update.Release.Version, modulePath, FileSafety.Hash(modulePath), setupPath, FileSafety.Hash(setupPath), update.Release.DownloadUrl);
                ValidatePackage(package);
                await File.WriteAllTextAsync(Path.Combine(pending, "release.json"), JsonSerializer.Serialize(package, JsonSettings.Options), cancellation);
                return new(package, $"ReShade {package.Version} downloaded from reshade.me and cached. No game files modified.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
                if (cached != null) return new(cached, $"New ReShade {update.Release.Version} download failed; using verified cached {cached.Version} instead.");
                throw new IOException("The official ReShade download could not be validated. No game files were modified. " + ex.Message, ex);
            }
        }
        finally { gate.Release(); }
    }
    public ReShadePackage? FindCached()
    {
        if (!Directory.Exists(cache)) return null;
        FileSafety.EnsureNoLinks(cache);
        List<ReShadePackage> packages = [];
        // Fixed-depth traversal avoids reparse points and arbitrary recursion under the cache.
        foreach (string version in Directory.EnumerateDirectories(cache).Take(100))
        {
            if ((File.GetAttributes(version) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string record in Directory.EnumerateDirectories(version).Take(100))
            {
                if ((File.GetAttributes(record) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    string manifest = Path.Combine(record, "release.json"); FileSafety.EnsureNoLinks(manifest);
                    if (!File.Exists(manifest) || new FileInfo(manifest).Length > 16384) continue;
                    var package = JsonSerializer.Deserialize<ReShadePackage>(File.ReadAllText(manifest), JsonSettings.Options);
                    if (package == null || !FileSafety.IsInside(record, package.ModulePath) || !FileSafety.IsInside(record, package.SetupPath)) continue;
                    ValidatePackage(package); packages.Add(package);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
            }
        }
        return packages.OrderByDescending(p => Version.Parse(p.Version)).FirstOrDefault();
    }
    public static void ValidatePackage(ReShadePackage package)
    {
        ValidateRelease(new(package.Version, package.DownloadUrl));
        FileSafety.EnsureNoLinks(package.SetupPath); FileSafety.EnsureNoLinks(package.ModulePath);
        if (new FileInfo(package.SetupPath).Length > 64 * 1024 * 1024 || new FileInfo(package.ModulePath).Length > 32 * 1024 * 1024 ||
            FileSafety.Hash(package.SetupPath) != package.SetupHash || FileSafety.Hash(package.ModulePath) != package.ModuleHash || !FileSafety.IsReShade(package.ModulePath))
            throw new InvalidDataException("Cached ReShade integrity check failed; download a fresh copy.");
        using var stream = new MemoryStream(ExtractModule(File.ReadAllBytes(package.SetupPath), package.Version));
        if (FileSafety.Hash(stream) != package.ModuleHash) throw new InvalidDataException("Cached DLL does not match the official setup archive.");
        var info = FileVersionInfo.GetVersionInfo(package.ModulePath);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != Version.Parse(package.Version))
            throw new InvalidDataException("ReShade DLL version does not match its release.");
    }
    public static byte[] ExtractModule(byte[] setup, string version)
    {
        if (setup.Length > 64 * 1024 * 1024 || setup.Length < 64 || setup[0] != 'M' || setup[1] != 'Z')
            throw new InvalidDataException("Invalid ReShade setup executable.");
        // ReShade's own setup locates its attached ZIP on a 512-byte boundary.
        for (int offset = 0; offset + 30 < setup.Length; offset += 512)
        {
            if (setup[offset] != 0x50 || setup[offset + 1] != 0x4b || setup[offset + 2] != 3 || setup[offset + 3] != 4) continue;
            using var archiveBytes = new MemoryStream(setup, offset, setup.Length - offset, false);
            using var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
            var module = zip.GetEntry("ReShade64.dll");
            if (module == null || zip.GetEntry("ReShade32.dll") == null || module.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Full ReShade setup archive is missing the expected DLLs.");
            using var input = module.Open(); using var output = new MemoryStream();
            byte[] buffer = new byte[65536]; int count;
            while ((count = input.Read(buffer)) != 0)
            {
                if (output.Length + count > 32 * 1024 * 1024) throw new InvalidDataException("ReShade DLL exceeds the extraction size limit.");
                output.Write(buffer, 0, count);
            }
            if (output.Length != module.Length) throw new InvalidDataException("Incomplete ReShade module.");
            byte[] bytes = output.ToArray(); using var image = new MemoryStream(bytes);
            if (!FileSafety.IsX64(image) || !FileSafety.ContainsMarker(image, "ReShade")) throw new InvalidDataException("Invalid 64-bit ReShade DLL.");
            return bytes;
        }
        throw new InvalidDataException("The official setup archive format changed. No game files were modified.");
    }
}
