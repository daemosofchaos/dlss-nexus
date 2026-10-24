using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Core;

public sealed class BundleManifest
{
    public string BundleVersion { get; set; } = "";
    public Dictionary<string, ConsumerSelection> ConsumerSelections { get; set; } = [];
    public List<Component> Components { get; set; } = [];
}

public sealed class ConsumerSelection
{
    public string PayloadPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Family { get; set; } = "";
}

public sealed class Component
{
    public string PayloadPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Version { get; set; } = "";
    public string SignatureStatus { get; set; } = "";
}

public sealed record GameTarget(string Root, string Directory, string Executable, string[] Proxies)
{
    public string DisplayName => Path.GetFileName(Executable);
    public string Detail => $"{Directory}  |  {(Proxies.Length == 0 ? "ReShade not installed" : string.Join(", ", Proxies.Select(Path.GetFileName)))}";
    public GraphicsApi DetectedApi => GraphicsApiDetection.Detect(Executable);
}

public sealed record DiscoveryResult(IReadOnlyList<GameTarget> Targets, IReadOnlyList<string> Notices, int DirectoriesScanned);
public enum Operation { Install, Replace, Keep, Current, Retire, Blocked, Removed }
public sealed record PlannedFile(string Component, string Destination, Operation Action, string? Source,
    string? ExpectedHash, string? OriginalHash, string Detail)
{
    public string OperationText => Action switch
    {
        Operation.Current => "Up to date", Operation.Keep => "Preserve", Operation.Retire => "Back up & remove",
        Operation.Blocked => "Approval needed", Operation.Removed => "Removed", _ => Action.ToString()
    };
    public bool ChangesFile => Action is Operation.Install or Operation.Replace or Operation.Retire;
}

public enum SetupRoute { Feeder, NativeDlss }

public sealed record InstallOptions(string Profile = "default", bool ReplaceExisting = false,
    bool ReplaceNeuralRuntime = false, string? ExperimentalAddon = null,
    ReShadePackage? ReShade = null, GraphicsApi GraphicsApi = GraphicsApi.Unknown,
    SetupRoute Route = SetupRoute.Feeder, bool NativeDlssConfirmed = false, bool DisableFeederEffects = false);

public sealed class InstallPlan
{
    public required GameTarget Target { get; init; }
    public required InstallOptions Options { get; init; }
    public required string Family { get; init; }
    public List<PlannedFile> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public Dictionary<string, string?> Guards { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool CanDisableFeederEffects { get; internal set; }
    public bool CanInstall => Errors.Count == 0;
    public bool UsesDx10Helper => InstallerEngine.UsesDx10Helper(Options);
    public bool UsesBundledNeuralRuntime => Files.Any(f => f.Component == "nvngx_dlssnr.dll" && f.ChangesFile);
}

public sealed record InstallResult(int FilesChanged, string? BackupDirectory, int FilesVerified = 0);
public sealed record BackupEntry(string Destination, string? OriginalHash, string? InstalledHash, string? BackupFile);
public sealed class InstallReceipt
{
    public string Product { get; set; } = "DLSS Nexus";
    public string Status { get; set; } = "Preparing";
    public string GameRoot { get; set; } = "";
    public string Executable { get; set; } = "";
    public SetupRoute Route { get; set; }
    public GraphicsApi GraphicsApi { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<BackupEntry> Files { get; set; } = [];
}

public static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
}