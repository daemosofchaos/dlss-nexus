using System.Text.Json;

namespace Nexus.Core;

public sealed partial class InstallerEngine(BundleCatalog catalog)
{
    public const string PresetName = "DLSS-Nexus.ini";
    public static readonly string[] GraphicsProxyNames = ["dxgi.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll", "vulkan-1.dll"];
    private static readonly HashSet<string> ConflictingAddons = new(StringComparer.OrdinalIgnoreCase)
        { "deep-fried-chicken.addon64", "alexs-toolkit.addon64" };

    public InstallPlan BuildPlan(GameTarget target, InstallOptions options, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        FileSafety.EnsureNoLinks(target.Directory);
        FileSafety.EnsureGameClosed(target.Executable);
        if (!FileSafety.IsInside(target.Root, target.Directory) || !FileSafety.IsX64(target.Executable) ||
            !Path.GetDirectoryName(target.Executable)!.Equals(target.Directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected game executable is no longer valid. Search again.");
        bool helper = UsesDx10Helper(options);
        bool installHostReShade = helper && HostNeedsReShade(target);
        bool installReShade = target.Proxies.Length == 0;
        if (!installReShade && target.Proxies.Any(p => !FileSafety.IsInside(target.Directory, p) ||
                !Path.GetDirectoryName(p)!.Equals(target.Directory, StringComparison.OrdinalIgnoreCase) || !FileSafety.IsReShade(p)))
            throw new InvalidOperationException("ReShade could not be verified. Install the 64-bit add-on build and search again.");
        if (installReShade)
        {
            if (options.ReShade == null) throw new InvalidOperationException("ReShade is missing. Prepare the official add-on download before previewing.");
            if (options.GraphicsApi is not (GraphicsApi.Direct3D10 or GraphicsApi.Direct3D11 or GraphicsApi.Direct3D12))
                throw new InvalidOperationException("Choose DirectX 10, 11 or 12 for this game before installing ReShade. Vulkan, OpenGL and DirectX 9 are not supported.");
            foreach (string name in GraphicsProxyNames)
            {
                string proxy = Path.Combine(target.Directory, name); FileSafety.EnsureNoLinks(proxy);
                if (File.Exists(proxy) || Directory.Exists(proxy))
                    throw new InvalidOperationException($"{name} already exists. Automatic ReShade setup will not overwrite or chain an existing graphics DLL. Use the official ReShade setup to resolve it, then search again.");
            }
            ReShadeService.ValidatePackage(options.ReShade);
        }
        if (installHostReShade)
        {
            if (options.ReShade == null) throw new InvalidOperationException("The DX10 helper needs a 64-bit ReShade download for host64. Prepare it before reviewing.");
            ReShadeService.ValidatePackage(options.ReShade);
        }
        if (options.Profile is not ("default" or "legacy" or "experimental"))
            throw new InvalidOperationException("Choose a supported installation profile.");
        string profile = helper && options.Profile == "default" ? "dx10" : options.Profile;
        if (!catalog.Manifest.ConsumerSelections.TryGetValue(profile, out var selection))
            throw new InvalidOperationException("Choose a supported installation profile.");
        string family = selection.Family;
        string consumerTarget = selection.TargetPath;
        string consumerSource = "payload/" + selection.PayloadPath;
        string consumerHash = catalog.GetComponent(selection.PayloadPath).Sha256;
        if (options.Profile == "experimental" && options.ExperimentalAddon is { } custom)
        {
            FileSafety.EnsureNoLinks(custom);
            family = FileSafety.AddonFamily(custom);
            consumerTarget = family == "Controller" ? "renodx-dlss.addon64" : "renodx-dlss5.addon64";
            consumerSource = "custom:" + Path.GetFullPath(custom);
            consumerHash = FileSafety.Hash(custom);
        }
        if (family is not ("Controller" or "Generic") ||
            consumerTarget != (family == "Controller" ? "renodx-dlss.addon64" : "renodx-dlss5.addon64"))
            throw new InvalidOperationException("The package add-on configuration is invalid.");
        if (helper && family != "Generic")
            throw new InvalidOperationException("DirectX 10 Feeder requires RenoDX Generic, not ShortFuse. Use Standard, Legacy or a compatible Generic custom build.");
        using (var addon = Open(consumerSource))
            if (FileSafety.AddonFamily(addon) != family)
                throw new InvalidOperationException("The selected add-on does not match the package configuration.");
        var plan = new InstallPlan { Target = target, Options = options, Family = family };
        bool feeder = options.Route == SetupRoute.Feeder;
        if (options.GraphicsApi == GraphicsApi.Unsupported)
            plan.Errors.Add("This graphics API is not supported. Use DirectX 10, 11 or 12 if the game offers it.");
        var reShadeConfiguration = ReShadeSetup.Prepare(target, options.Route);
        var paths = ReShadePaths.Resolve(target, plan.Warnings, configurationText: reShadeConfiguration.Text);
        GameTarget? hostTarget = helper ? Dx10HostTarget(target) : null;
        var hostPaths = hostTarget == null ? null : ReShadePaths.Resolve(hostTarget, plan.Warnings, includeShaders: false);
        if (hostTarget != null)
        {
            FileSafety.EnsureGameClosed(hostTarget.Executable);
            if (paths.AddonSearchDirectories.Any(d => FileSafety.IsInside(hostTarget.Root, d)) ||
                paths.RecursiveAddonRoots.Any(d => FileSafety.IsInside(d, hostTarget.Root)))
                plan.Errors.Add("The game's AddonPath would also load host64 add-ons. Use a non-recursive game add-on path that excludes host64, then review again.");
            if (!hostPaths!.AddonSearchDirectories.Contains(hostTarget.Directory, StringComparer.OrdinalIgnoreCase))
                plan.Errors.Add("The helper's AddonPath must include its own host64 folder so the neural consumer can be found.");
            foreach (var guard in GameConfiguration.Read(hostTarget, hostPaths!.AddonSearchDirectories).Guards)
                plan.Guards[guard.Key] = guard.Value;
            plan.Warnings.Add("DirectX 10 Feeder (beta) uses a 64-bit helper and RenoDX Generic in host64. Configure depth in the game and Neural Rendering in the helper panel. DLAA at native size only; GPU and game compatibility varies.");
            if (options.Profile == "legacy")
                plan.Warnings.Add("Legacy Generic v4.7 has known neural-evaluation failures on newer NVIDIA drivers, including 616.64 and 617.14. Prefer the Standard DX10 helper build or a compatible Generic custom build.");
        }
        var configuration = GameConfiguration.Read(target, paths.AddonSearchDirectories, reShadeConfiguration.Text);
        string configPath = FileSafety.SafePath(target.Directory, "ReShade.ini");
        if (configuration.Guards[configPath] != reShadeConfiguration.OriginalHash)
            throw new IOException("ReShade.ini changed while preparing the review. Generate a new preview.");
        foreach (var guard in configuration.Guards) plan.Guards[guard.Key] = guard.Value;
        if (!feeder)
        {
            if (!options.NativeDlssConfirmed) plan.Errors.Add("Confirm that this game has a DLSS option in its graphics settings.");
            if (family != "Controller") plan.Errors.Add("Native DLSS setup requires the ShortFuse add-on. Select Standard under Advanced.");
            if (options.GraphicsApi == GraphicsApi.Direct3D10)
                plan.Errors.Add("DirectX 10 uses the Feeder helper route, not native DLSS. Select ‘No’ for native DLSS support or choose the game's DX11/DX12 mode.");
            else if (options.GraphicsApi is not (GraphicsApi.Direct3D11 or GraphicsApi.Direct3D12))
                plan.Errors.Add("Select the DirectX version used by this game under Advanced.");
            if (configuration.FeederEffectEnabled == true && !options.DisableFeederEffects)
            {
                plan.CanDisableFeederEffects = true;
                plan.Errors.Add("Feeder is enabled in the active ReShade preset. Turn off Feeder for this setup before installing native DLSS.");
            }
            plan.Warnings.Add("Enable DLSS in the game's graphics settings. Add-on and driver compatibility varies by game.");
        }
        if (options.Profile != "default") plan.Warnings.Add("Custom and Legacy builds may need different in-game settings.");
        plan.Warnings.Add(installReShade
            ? $"ReShade {options.ReShade!.Version}. Single-player only; do not use with anti-cheat."
            : "Check that ReShade has full add-on support.");

        void Add(string label, string destination, string source, string hash, bool runtime = false, bool config = false)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!FileSafety.IsInside(target.Root, destination)) throw new InvalidOperationException("An installation path is outside the game root.");
            FileSafety.EnsureNoLinks(destination);
            if (Directory.Exists(destination)) throw new InvalidOperationException($"A folder occupies the destination for {label}.");
            using (var input = Open(source))
                if (!FileSafety.Hash(input).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Integrity check failed for {label}. Obtain a fresh installer or select the add-on again.");
            string? original = FileSafety.ExistingHash(destination);
            Operation action;
            string detail;
            if (original == hash) { action = Operation.Current; detail = "Matches this package"; }
            else if (original == null) { action = Operation.Install; detail = "New component"; }
            else if (config || (runtime && (label != "nvngx_dlssnr.dll" || !options.ReplaceNeuralRuntime)))
            { action = Operation.Keep; detail = config ? "Existing game settings preserved" : "Existing runtime preserved"; }
            else if (!options.ReplaceExisting)
            { action = Operation.Blocked; detail = "Enable replacement with verified backups"; }
            else { action = Operation.Replace; detail = "Original file will be backed up"; }
            plan.Files.Add(new(label, destination, action, source, hash, original, detail));
            if (action == Operation.Blocked) plan.Errors.Add($"{label} differs. To update it, enable ‘Replace existing add-ons’ under Advanced.");
        }
        if (installReShade)
        {
            Add("ReShade (dxgi.dll)", Path.Combine(target.Directory, "dxgi.dll"), "custom:" + options.ReShade!.ModulePath, options.ReShade.ModuleHash);
        }
        byte[] configBytes = System.Text.Encoding.UTF8.GetBytes(reShadeConfiguration.Text);
        using (var memory = new MemoryStream(configBytes))
        {
            string hash = FileSafety.Hash(memory);
            var action = reShadeConfiguration.OriginalHash == hash ? Operation.Current :
                reShadeConfiguration.OriginalHash == null ? Operation.Install : Operation.Replace;
            plan.Files.Add(new("ReShade.ini", configPath, action, "generated:" + Convert.ToBase64String(configBytes),
                hash, reShadeConfiguration.OriginalHash, action == Operation.Replace
                    ? "Shader paths and missing default preset repaired; other settings retained" : "Shader, texture and preset search paths"));
        }
        if (hostTarget != null)
            AddDx10Host(plan, paths, hostTarget, installHostReShade, Add);
        foreach (var component in catalog.Manifest.Components.Where(c =>
            !c.PayloadPath.StartsWith("experimental/") && !c.PayloadPath.StartsWith("legacy/") &&
            !c.PayloadPath.StartsWith("dx10/") &&
            !c.PayloadPath.StartsWith("templates/") && c.PayloadPath != "renodx-dlss.addon64"))
        {
            string relative = component.TargetPath;
            if (!feeder && relative != "nvngx_dlssnr.dll" && !relative.StartsWith("reshade-shaders/")) continue;
            if (helper && relative == "dlss5-feed.addon64") continue;
            if (relative.Length == 0) throw new InvalidOperationException("A package destination is missing.");
            string destination;
            if (relative.StartsWith("reshade-shaders/Shaders/"))
                destination = FileSafety.SafePath(paths.Shaders, relative[24..]);
            else if (relative.StartsWith("reshade-shaders/Textures/"))
                destination = FileSafety.SafePath(paths.Textures, relative[25..]);
            else if (relative.EndsWith(".addon64") || relative == "dlss5-feed.cfg") destination = FileSafety.SafePath(paths.Addons, relative);
            else destination = FileSafety.SafePath(helper && (relative is "nvngx_dlss.dll" or "nvngx_dlssnr.dll") ? hostTarget!.Directory : target.Directory, relative);
            Add(Path.GetFileName(relative), destination, "payload/" + component.PayloadPath, component.Sha256,
                relative is "nvngx_dlss.dll" or "nvngx_dlssnr.dll", relative == "dlss5-feed.cfg");
        }
        string consumerDirectory = hostTarget?.Directory ?? paths.Addons;
        Add(consumerTarget, FileSafety.SafePath(consumerDirectory, consumerTarget), consumerSource, consumerHash);
        if (feeder) Add(PresetName, FileSafety.SafePath(target.Directory, PresetName), "templates/" + PresetName, catalog.VerifyPreset());
        else
        {
            byte[] preset = System.Text.Encoding.UTF8.GetBytes(ReShadeSetup.NativePreset);
            using var memory = new MemoryStream(preset);
            Add(PresetName, FileSafety.SafePath(target.Directory, PresetName), "generated:" + Convert.ToBase64String(preset), FileSafety.Hash(memory), config: true);
        }
        string alternate = consumerTarget == "renodx-dlss.addon64" ? "renodx-dlss5.addon64" : "renodx-dlss.addon64";
        if (!feeder && configuration.FeederEffectEnabled == true && options.DisableFeederEffects)
        {
            var migration = NativePresetMigration.Prepare(target, configuration);
            int existing = plan.Files.FindIndex(f => f.Destination.Equals(migration.Destination, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) plan.Files.Add(migration); else plan.Files[existing] = migration;
        }
        var addonDirs = paths.AddonSearchDirectories.Append(target.Directory)
            .Concat(hostPaths?.AddonSearchDirectories ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in addonDirs)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string addon in Directory.GetFiles(directory, "*.addon64"))
            {
                FileSafety.EnsureNoLinks(addon);
                string name = Path.GetFileName(addon);
                bool neural = name.StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase);
                bool activeDestination = addon.Equals(FileSafety.SafePath(consumerDirectory, consumerTarget), StringComparison.OrdinalIgnoreCase);
                bool feed = name.Equals("dlss5-feed.addon64", StringComparison.OrdinalIgnoreCase);
                bool feedHelper = name.Equals("dlss5-feed-helper.addon64", StringComparison.OrdinalIgnoreCase);
                bool activeHelper = helper && addon.Equals(FileSafety.SafePath(paths.Addons, "dlss5-feed-helper.addon64"), StringComparison.OrdinalIgnoreCase);
                if (name.Equals(alternate, StringComparison.OrdinalIgnoreCase) || (helper && neural && !activeDestination) ||
                    (feed && (!feeder || helper)) || (feedHelper && !helper))
                {
                    plan.Files.Add(new(name, addon, options.ReplaceExisting ? Operation.Retire : Operation.Blocked, null, null,
                        FileSafety.Hash(addon), "Previous neural add-on; retained in the backup"));
                    if (!options.ReplaceExisting) plan.Errors.Add("A different setup is installed. Enable ‘Replace existing add-ons’ to back up and remove it.");
                }
                else if (ConflictingAddons.Contains(name) || (neural && !activeDestination) || (feedHelper && !activeHelper) ||
                    (name.Equals("dlss5-feed.addon64", StringComparison.OrdinalIgnoreCase) &&
                     !addon.Equals(FileSafety.SafePath(paths.Addons, name), StringComparison.OrdinalIgnoreCase)))
                    plan.Errors.Add($"Conflicting add-on found: {addon}. Disable or remove the duplicate before installing.");
            }
        }
        if (plan.Files.Select(f => f.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Count)
            throw new InvalidOperationException("Two components map to the same destination. Review the ReShade search paths.");
        foreach (var guard in target.Proxies.Concat(GraphicsProxyNames.Select(n => Path.Combine(target.Directory, n)))
                     .Append(target.Executable).Append(Path.Combine(target.Directory, "ReShade.ini")))
        { FileSafety.EnsureNoLinks(guard); plan.Guards[guard] = FileSafety.ExistingHash(guard); }
        if (hostTarget != null)
            foreach (string guard in HostProxyNames.Select(n => Path.Combine(hostTarget.Directory, n)).Append(hostTarget.Executable))
            { FileSafety.EnsureNoLinks(guard); plan.Guards[guard] = FileSafety.ExistingHash(guard); }
        return plan;
    }

    private Stream Open(string source) => source.StartsWith("custom:", StringComparison.Ordinal)
        ? File.OpenRead(source[7..]) : source.StartsWith("generated:", StringComparison.Ordinal)
        ? new MemoryStream(Convert.FromBase64String(source[10..]), false) : catalog.OpenSource(source);

    public InstallResult Install(InstallPlan plan, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (!plan.CanInstall) throw new InvalidOperationException("Resolve the review items before installing.");
        // Rebuild the plan so file changes, duplicate add-ons and configuration edits cannot silently invalidate a preview.
        var fresh = BuildPlan(plan.Target, plan.Options, cancellation);
        if (!fresh.CanInstall || !fresh.Files.SequenceEqual(plan.Files) ||
            plan.Guards.Any(g => FileSafety.ExistingHash(g.Key) != g.Value))
            throw new InvalidOperationException("The game folder or package changed after review. Generate a new preview before installing.");
        var changes = plan.Files.Where(f => f.ChangesFile).ToArray();
        if (changes.Length == 0) return new(0, null, plan.Files.Count(f => f.Action is Operation.Current or Operation.Keep));
        var backup = FileSafety.SafePath(plan.Target.Directory,
            "DLSS-Nexus-Backups/" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var receipt = new InstallReceipt { GameRoot = plan.Target.Root, Executable = plan.Target.Executable, Route = plan.Options.Route, GraphicsApi = plan.Options.GraphicsApi };
        string receiptPath = Path.Combine(backup, "installation.json");
        Dictionary<string, string> stages = new(StringComparer.OrdinalIgnoreCase);
        List<PlannedFile> applied = [];
        void SaveReceipt() => File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, JsonSettings.Options));
        try
        {
            Directory.CreateDirectory(backup);
            for (int i = 0; i < changes.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var file = changes[i];
                progress?.Report($"Preparing {file.Component}");
                string? copy = null;
                if (file.OriginalHash != null)
                {
                    // A backup must not remain loadable through a recursive ReShade add-on search path.
                    copy = Path.Combine(backup, "originals", Path.GetRelativePath(plan.Target.Root, file.Destination) + ".backup");
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file.Destination, copy, false);
                    if (FileSafety.Hash(copy) != file.OriginalHash) throw new IOException("A file changed while its backup was being created. Review the installation again.");
                }
                if (file.Action != Operation.Retire)
                {
                    string stage = Path.Combine(backup, "staging", i.ToString());
                    Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                    using (var source = Open(file.Source!))
                    using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                    if (FileSafety.Hash(stage) != file.ExpectedHash) throw new IOException($"Integrity check failed for {file.Component}. Installation was not applied.");
                    stages[file.Destination] = stage;
                }
                receipt.Files.Add(new(file.Destination, file.OriginalHash, file.ExpectedHash, copy == null ? null : Path.GetRelativePath(backup, copy)));
            }
            receipt.Status = "Prepared"; SaveReceipt();
            cancellation.ThrowIfCancellationRequested();
            var preparedReview = BuildPlan(plan.Target, plan.Options, cancellation);
            if (!preparedReview.CanInstall || !preparedReview.Files.SequenceEqual(plan.Files))
                throw new IOException("Components or active add-ons changed during preparation. Generate a new preview.");
            FileSafety.EnsureGameClosed(plan.Target.Executable);
            foreach (var file in plan.Files)
            {
                FileSafety.EnsureNoLinks(file.Destination);
                if (FileSafety.ExistingHash(file.Destination) != file.OriginalHash)
                    throw new IOException("The game folder changed during preparation. Generate a new preview.");
            }
            foreach (var guard in plan.Guards)
                if (FileSafety.ExistingHash(guard.Key) != guard.Value) throw new IOException("The game configuration changed during preparation. Generate a new preview.");
            receipt.Status = "Installing"; SaveReceipt();
            // Once changes start, finish or roll back as one operation; cancellation is intentionally deferred.
            foreach (var file in changes.OrderBy(f => f.Action == Operation.Retire ? 1 : 0))
            {
                progress?.Report($"Installing {file.Component}");
                FileSafety.EnsureNoLinks(file.Destination);
                if (file.Action == Operation.Retire) File.Delete(file.Destination);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                    // The staged file is on the same volume as the destination, avoiding partially copied DLLs.
                    File.Move(stages[file.Destination], file.Destination, file.OriginalHash != null);
                }
                applied.Add(file);
                if (FileSafety.ExistingHash(file.Destination) != file.ExpectedHash) throw new IOException($"Installation verification failed for {file.Component}.");
            }
            // Completion is reported only after checking every selected file, including protected originals.
            foreach (var file in plan.Files)
                if (FileSafety.ExistingHash(file.Destination) != (file.Action == Operation.Keep ? file.OriginalHash : file.ExpectedHash))
                    throw new IOException($"Final file verification failed for {file.Component}.");
            foreach (var guard in plan.Guards.Where(g => !plan.Files.Any(f => f.Destination.Equals(g.Key, StringComparison.OrdinalIgnoreCase) && f.ChangesFile)))
                if (FileSafety.ExistingHash(guard.Key) != guard.Value) throw new IOException("A protected game file changed during installation.");
            receipt.Status = "Complete"; SaveReceipt();
            if (Directory.Exists(Path.Combine(backup, "staging"))) Directory.Delete(Path.Combine(backup, "staging"), true);
            progress?.Report("Installation complete. Original files and the installation receipt are retained in the backup folder.");
            return new(changes.Length, backup, plan.Files.Count(f => f.Action != Operation.Retire));
        }
        catch (Exception failure)
        {
            List<string> recoveryErrors = [];
            foreach (var file in applied.AsEnumerable().Reverse())
            {
                try
                {
                    FileSafety.EnsureNoLinks(file.Destination);
                    if (FileSafety.ExistingHash(file.Destination) != file.ExpectedHash)
                        throw new IOException("The file changed outside this installation; automatic recovery will not overwrite it.");
                    var entry = receipt.Files.Single(e => e.Destination == file.Destination);
                    if (entry.BackupFile != null)
                    {
                        string original = FileSafety.SafePath(backup, entry.BackupFile);
                        if (FileSafety.Hash(original) != entry.OriginalHash) throw new IOException("Backup verification failed.");
                        File.Copy(original, file.Destination, true);
                    }
                    else File.Delete(file.Destination);
                    if (FileSafety.ExistingHash(file.Destination) != file.OriginalHash) throw new IOException("Recovery verification failed.");
                }
                catch (Exception ex) { recoveryErrors.Add($"{file.Component}: {ex.Message}"); }
            }
            receipt.Status = recoveryErrors.Count == 0 ? "Rolled back" : "Recovery required";
            if (Directory.Exists(backup)) SaveReceipt();
            throw new IOException(recoveryErrors.Count == 0
                ? $"Installation stopped. Any applied file changes were rolled back. {failure.Message}"
                : $"Installation stopped; manual recovery is required. Backups: {backup}. {string.Join("; ", recoveryErrors)}", failure);
        }
    }
}