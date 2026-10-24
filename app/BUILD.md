# Build and test

## Prerequisites

Windows 10/11 with the **.NET 10 SDK**. Release packaging additionally uses [Inno Setup 7](https://jrsoftware.org/isinfo.php). Visual Studio with .NET desktop development is optional. The installed application requires neither an external .NET runtime nor PowerShell.

## Layout

| Location | Purpose |
|---|---|
| Nexus | WPF wizard, native pickers, embedded resources and diagnostics |
| Nexus.Core | Discovery, game inventory/local state, native/Feeder plans, ReShade cache, verification and recovery |
| Nexus.Tests | Dependency-free isolated synthetic core regression harness |
| Nexus.UiTests | Offscreen wizard rendering/navigation and isolated installation fixtures |
| Assets | Product catalog, original generated icon and shared branding |
| ../build | Current release packaging script and Inno Setup source |

## Source-only checks

Run from the repository root, using fresh output directories for each test run:

```powershell
dotnet build app/Nexus.sln -c Release
dotnet run --project app/Nexus.Tests/Nexus.Tests.csproj -c Release --no-build -- app/TestResults/core-new
dotnet run --project app/Nexus.UiTests/Nexus.UiTests.csproj -c Release --no-build -- app/TestResults/wizard-new
```

No local deployment payloads, network or real game are needed. UI tests use an inert synthetic catalog by default. Test fixtures and JSON reports are retained in the ignored output directory. Windows CI runs this flow without publishing binary artifacts.

Core tests cover launcher discovery, PE identity/API hints, bounded component/version scans, atomic local library updates, reported feature settings, configuration fingerprints, native/Feeder switching, DX10 helper placement and API persistence, safe local paths, conflicts, runtime/config preservation, explicit replacement, stale plans and failure recovery. DX10 cases also cover game/host ReShade setup, helper isolation and transactional rollback without starting the helper or evaluating neural rendering. Offscreen WPF tests cover Games/Setup/Review/Ready, full/minimum/125% renders, fixed navigation, library details, settings reports, Advanced defaults, Back/Next/state invalidation, reduced motion and isolated-fixture completion, including failed library saves. Launch actions use injected process callbacks; tests validate executable paths, working directories and failure handling without starting games.

ReShade regression cases verify shader/include/texture deployment and preset selection for both routes, inactive native presets, incomplete-install repair, custom-setting preservation, UTF-8/CRLF configuration handling, stale reviews and rollback of shader/configuration changes. Native migration cases cover explicit Feeder deactivation, selected-preset backups, duplicate technique entries, BOM/Unicode preservation, unrelated effects, stale reviews, rollback and safety guards. UI cases verify visible blockers, separate warnings, guided resolution and consent reset. Native maximize geometry tests cover each taskbar edge and negative-origin monitors in physical pixels.

### Isolated desktop checks

With authorized payloads locally present, run the desktop harness using a fresh output directory:

```powershell
dotnet run --project app/Nexus.UiTests/Nexus.UiTests.csproj -c Release -- app/TestResults/desktop-new --desktop-smoke
```

It opens an isolated **Native migration fixture**, with its own game files, library and cache. Game launches are disabled. Use it to check maximized/restored Games, Setup, blocked/resolved Review and Ready, including footer reachability above the taskbar. Actual monitor bounds, work area, window DPI and footer-button bounds are saved in `desktop-metrics.jsonl`. The offscreen 125% render is a layout check, not a physical high-DPI desktop test.

## Optional full local tests

For real archive-extraction/cache cases, supply a locally downloaded official **full add-on** ReShade setup as the second harness argument. The current integration fixture uses 6.8.0. Mock HTTP serves these archive bytes; tests do not make live requests or install into real games.

```powershell
dotnet run --project app/Nexus.Tests/Nexus.Tests.csproj -c Release -- app/TestResults/core-full "C:\Fixtures\ReShade_Setup_6.8.0_Addon.exe"
dotnet run --project app/Nexus.UiTests/Nexus.UiTests.csproj -c Release -- app/TestResults/wizard-full "C:\Fixtures\ReShade_Setup_6.8.0_Addon.exe"
# With authorized payloads locally present, also test the actual embedded catalog:
dotnet run --project app/Nexus.UiTests/Nexus.UiTests.csproj -c Release -- app/TestResults/wizard-embedded "C:\Fixtures\ReShade_Setup_6.8.0_Addon.exe" --embedded
```

Without an archive, those integration cases are explicitly skipped. Without local deployment assets, do not use `--embedded`.

## Local production package

Place only authorized payloads under the ignored root `payload/` folder, matching every exact SHA-256 value in `app/Assets/catalog.json`. The checked-in preset under `templates/` is verified too. Keep binary and shader releases matched; do not promote experiments implicitly.

```powershell
./build/Build-Release.ps1 -IsccPath "C:\Tools\Inno Setup 7\ISCC.exe"
```

The development-only script derives the version from the application project, verifies sources, publishes the self-contained EXE, verifies the published embedded package, includes notices, compiles Inno Setup, creates the portable ZIP and writes SHA-256 manifests. It refuses to overwrite existing output. Inno Setup's compiler is a development prerequisite, not included in the app, and remains subject to its licensing terms.

Outputs under ignored `dist/`:

```text
DLSS-Nexus-<version>-win-x64/
DLSS-Nexus-<version>-win-x64.zip
DLSS-Nexus-<version>-installer.exe
SHA256SUMS-<version>.txt
```

Direct publishing is also guarded: `dotnet publish app/Nexus/Nexus.csproj -c Release` fails when embedded sources are absent or mismatched. Source-only builds are intentionally allowed for CI/testing but are not usable production installers. WPF trimming is disabled; the single-file app includes the .NET runtime and native self-extraction support.

The Inno installer is per-user and creates an application uninstaller. Game setup is separate. No broad uninstall deletion is used. Game files, backups, the saved library, shared cache and unrelated files survive application uninstall.

## Read-only diagnostics

```powershell
./DLSS-Nexus.exe --verify-package package-report.json
./DLSS-Nexus.exe --scan "D:\Games\Example" --report discovery-report.json
./DLSS-Nexus.exe --inventory "D:\Games\Example" --report inventory-report.json
./DLSS-Nexus.exe --list-games --report library-report.json
./DLSS-Nexus.exe --prepare-reshade --report cache-report.json
```

These do not install into games. Inventory reports contain file paths, hashes, installed versions and known ReShade configuration state; they do not update the saved library or establish live feature activity. Cache preparation downloads/extracts only into the app cache; other commands write their requested reports (plus normal .NET startup extraction). There is no unattended game-installation CLI.

The GUI stores game records and user-reported settings in `%LOCALAPPDATA%/DLSS-Nexus/library.json`. Component or known configuration changes mark old reports as needing a recheck. State stays local; this application does not send it to a service.

## Assets and release scope

Original branding uses a generated master PNG, in-app mark and seven-frame ICO (16/24/32/48/64/128/256). `tools/build_icon.py` with Pillow is an optional asset-development helper, never an application dependency.

Local possession or a matching hash does not establish redistribution permission. Source and binary distribution remain subject to the applicable [third-party notices](../THIRD-PARTY-NOTICES.md) and licensing terms. CI intentionally contains no payload download or release-publishing steps.
