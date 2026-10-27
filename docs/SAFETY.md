# Compatibility and recovery

## Compatibility

- Windows 10/11, local writable game folders, 64-bit DirectX 11/12, plus the DirectX 10 Feeder helper route (beta).
- No anti-cheat-protected games. Full ReShade add-on support can cause multiplayer bans.
- Vulkan, OpenGL, DirectX 9, 32-bit games and deployment outside the selected game root are not supported by Nexus.
- GPU/driver support varies with the exact runtime and add-on. A listed game is not a compatibility guarantee.

Native setup uses ShortFuse and requires a DLSS option in the game. Feeder setup supplies inputs for games without native DLSS. Finding a DLSS DLL does not establish which setup is appropriate.

DX10/DX10.1 setup uses Feeder's 64-bit helper, not its regular in-process 64-bit add-on. The helper starts when the game's Feeder begins supplying frames; Nexus's **Launch game** button still starts only the game executable. The helper's ReShade, Generic add-on and runtimes are kept in `host64` beside the Feeder add-on. It requires a compatible GPU/driver and runs at native resolution (DLAA), not engine-integrated upscaling.

The default DX10 Generic build is v7.0.0-rc8. Legacy v4.7 has upstream-reported neural-evaluation failures on newer NVIDIA drivers, including 616.64 and 617.14. Nexus warns when that option is selected; installing the files is not evidence that neural evaluation works.

Nexus does not install Super Resolution, Ray Reconstruction or Frame Generation support into games that lack those features. Their existing files and your reported settings can be tracked.

## Package components

DLSS Nexus **1.1.0** includes the following injection components:

| Component | Version | Date |
|---|---|---|
| Feeder `dlss5-feed.addon64` | 1.19.0 | [11 October 2026](https://github.com/jlrouzies-fr/DLSS5-Feeder/releases/tag/v1.19.0) |
| Feeder `DLSS5_Feed.fx` | 1.19.0 | 11 October 2026 |
| DX10 `dlss5-feed-helper.addon64` | 1.19.0 | 11 October 2026 |
| DX10 `host64/dlss5-feed-host64.exe` | 1.19.0 | 11 October 2026 |
| Standard ShortFuse `renodx-dlss.addon64` | `0.2026.1016.1704` | 16 October 2026 |
| Previous ShortFuse `versions/shortfuse-20261003/renodx-dlss.addon64` | `0.2026.1003.2333` | — |
| Custom ShortFuse `renodx-dlss.addon64` | `0.2026.1016.1704` | 16 October 2026 |
| Default DX10 Generic `renodx-dlss5.addon64` | v7.0.0-rc8 (`0.2026.0923.0307`) | [23 September 2026](https://github.com/RankFTW/rhi-repo/releases/tag/renodx-dlss5-7.0.0-rc8) |
| Legacy Generic `renodx-dlss5.addon64` | v4.7 (`0.2026.0828.0517`) | [1 September 2026](https://github.com/RankFTW/rhi-repo/releases/tag/renodx-dlss5-4.70) |
| Super Resolution `nvngx_dlss.dll` | `310.8.0.0` · NVIDIA-signed | [27 August 2026](https://github.com/RankFTW/rhi-repo/releases/tag/dlss-310.8.0) |
| Neural Rendering `nvngx_dlssnr.dll` — ShortFuse compatibility build | `310.9.16.0` · patched FP16 · **unsigned** | 16 October 2026 |
| Original NVIDIA Neural Rendering `versions/nr-310.8.0/nvngx_dlssnr.dll` | `310.8.0.0` · NVIDIA-signed | — |

The previous add-on is a ShortFuse/Controller build, despite its supplied filename `renodx-dlss5.addon64`. Nexus installs it under the canonical ShortFuse name `renodx-dlss.addon64`; it is not the Generic DX10 add-on. No upstream release date is asserted for these two supplied alternatives.

### RTX 20, 30 and 40 series support

Nexus includes **ShortFuse's patched FP16 DLSS Neural Rendering runtime** for **GeForce RTX 20, RTX 30 and RTX 40 series GPUs**. This DLL provides Neural Rendering compatibility for those card generations.

**Build:** `compat-fp16-310.9.16-cuda13.4.3`.

To use the included compatibility DLL in a game that already has a neural runtime, open **Advanced**, enable **Replace existing add-ons** and **Also replace the neural runtime**, then review the changes. Nexus backs up the replaced files.

**DLSS-NR 310.8.0.0 is the original signed NVIDIA build, not this patched FP16 build.** Do not assume the same RTX 20/30/40 compatibility. GPU and driver requirements can differ. Image-quality preferences between builds are subjective and are not measured by Nexus.

### Selecting versions safely

Under **Advanced**, select **Add-on version** and **DLSS-NR version** independently. New setups default to the current builds; reopening a setup restores uniquely recognized installed versions by their exact hashes. Standard DX10 offers only compatible Generic choices; Custom uses your custom add-on instead of a bundled add-on choice.

Only the selected add-on and runtime are planned, never every bundled alternative. Version selection itself is not replacement consent. A different existing add-on requires **Replace existing add-ons**. Changing an existing neural runtime requires that option and **Also replace the neural runtime**. Otherwise Review explicitly shows **Keep** and says the selected runtime alternative will not be installed. Downgrades use the same backups, stale-review checks and guarded rollback as upgrades.

### Supporting shaders and configuration

Both setup routes include the LumeniteFX kernel and supporting shaders. The native-DLSS preset leaves effects disabled; the Feeder preset enables the required effects:

| Shader / include | Version | Date |
|---|---|---|
| `lumenite_Kernel.fx` | `2026.07.28` | 28 July 2026 |
| `include/lumenite_Compute.fxh` | `2026.05.09` | 9 May 2026 |
| `include/lumenite_Helpers.fxh` | `2026.05.30` | 30 May 2026 |
| `include/lumenite_Projections.fxh` | `2026.04.11` | 11 April 2026 |

The package also includes the Feeder configuration, ReShade include headers, DrawText helper, blue-noise texture and Nexus preset. The [complete component catalog](https://github.com/daemosofchaos/dlss-nexus/blob/main/app/Assets/catalog.json) lists all included files and their checksums.

### Checksums

SHA-256 checksums for the bundled add-ons, runtimes and main shaders:

```text
2B8C905012FD464DDF0554C6B178CBA55030EC10EB917ACDCEE0E7A2376FE4DD  dlss5-feed.addon64
C069AA9D8B01FCEE34234BC458A798C3BE6F27FC8B9F23B74B5FB1784A828886  dx10/dlss5-feed-helper.addon64
79E427430C5C0102F6C7A557155B706AD51B0C40933F6E245FCE922BF5B1401E  dx10/host64/dlss5-feed-host64.exe
A8FD34B531FD22C3DBD008ED8FD3CBFCCA2FB451729E19CD1FC2E4C85B5068D4  renodx-dlss.addon64
A8FD34B531FD22C3DBD008ED8FD3CBFCCA2FB451729E19CD1FC2E4C85B5068D4  experimental/renodx-dlss.addon64
F43A19C7DEBE2225AEEE68B23B804939F2222E128775080F09F18D09B83D1736  versions/shortfuse-20261003/renodx-dlss.addon64
FF8B9738738265E09F01A1C470A0CB0A59EB021B2B23797C3DF7CEA722A724B6  dx10/renodx-dlss5.addon64
D5ADF82EB44B065F4C590AC91FE824BAB07AFEA0EB9F994BDE936710C8593952  legacy/renodx-dlss5.addon64
C85F971CE023C9F3492FC7455F0B01A24BA18EA39636407A846902C4360B0B7E  nvngx_dlss.dll
CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED  nvngx_dlssnr.dll
E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E  versions/nr-310.8.0/nvngx_dlssnr.dll
C4BA1610DF8E1593FAA7C203D6D0517058D3A8ECF2DFDA18CA67849844391456  reshade-shaders/Shaders/DLSS5_Feed.fx
463D2BADA33FD3058383BC0273ABA74DF9D5F1DB75FE13D8F305A22BF94526E3  reshade-shaders/Shaders/lumenite_Kernel.fx
```

Nexus carries these files inside the application and installs the selected components during game setup. Use **Check status** to view each game's installed versions.

### Which files are used

- **Native DLSS, DX11/DX12:** Standard ShortFuse plus the neural runtime when needed, shader/include/texture files and an inactive Nexus preset. Uses the game's native DLSS; no Feeder add-on, Feeder configuration or Super Resolution DLL is deployed by Nexus.
- **Feeder, DX11/DX12:** regular Feeder add-on, matched shader/preset/support assets, selected neural add-on and runtime files. Existing Super Resolution and neural runtimes are kept unless the supported explicit replacement choice applies.
- **Feeder, DX10:** helper in the game's add-on location, with host executable, Generic add-on and runtimes under `host64`. Standard uses v7.0.0-rc8; Legacy uses v4.7. The regular in-process Feeder add-on and ShortFuse are not the DX10 route.
- **Custom — testing:** includes the same ShortFuse build as Standard and lets you select a different add-on. Choose a compatible Generic build for DX10.
- **ReShade:** Nexus keeps existing ReShade or downloads the official full add-on version when needed. Review and Check status show the installed version.

The app and default patched neural runtime are unsigned. The original NVIDIA 310.8.0.0 alternative has a valid NVIDIA signature. A valid signature is not a gameplay or GPU-compatibility guarantee. Do not disable security protections to bypass a Windows warning.

## Existing files

Nexus keeps existing ReShade, game settings and Super Resolution runtimes. Neural-runtime replacement requires a separate choice. Replaced files and removed add-ons are backed up first.

ReShade setup adds missing shader/texture paths and keeps existing search entries. When the default ReShade preset file is missing, it selects the Nexus preset. Custom settings and existing presets are preserved; configuration repairs are included in Review and backed up before installation.

Unrelated graphics proxies are not overwritten or chained automatically. Duplicate neural add-ons block setup. Linked folders and external preset/deployment paths are rejected.

When switching from Feeder to native DLSS, Review blocks installation while **DLSS5_Feed** is enabled in the active preset. Choose **Turn off Feeder for this setup** to include a backed-up preset change in the installation, or turn the effect off in ReShade and choose **Recheck**. Nexus removes only the Feeder technique entries; other effects, settings, preset selections and shader files are retained. **Replace existing add-ons** separately authorizes backing up and removing the previous add-ons. Preset changes after Review require a new review.

## Library status

Version information comes from file metadata or an exact package-hash match. Unknown versions stay **Unknown**. Scans are bounded and may be incomplete; unreadable folders and scan limits are reported.

| Status | Meaning |
|---|---|
| Installed | A valid 64-bit component file was found. Not proof it is loaded. |
| Enabled · reported | You recorded the feature as enabled after checking the game. |
| Disabled · reported | You recorded the feature as disabled. |
| Recheck needed | Files or known ReShade configuration changed since the report. |
| Not checked / Unknown | No reliable record is available. |

Preset status describes the selected local ReShade preset, not live rendering. Nexus does not read every game's settings, attach to game processes or verify neural evaluations. Reports may also become outdated after game/driver/settings changes not visible to the scanner.

The local library is stored at `%LOCALAPPDATA%\DLSS-Nexus\library.json`. It contains game paths, component hashes and feature reports. Review those paths before sharing the file or diagnostics.

## ReShade

Missing ReShade is downloaded from [reshade.me](https://reshade.me/) and stored under `%LOCALAPPDATA%\DLSS-Nexus\Cache\ReShade`. A verified cached copy can be used offline. Corrupted downloads are rejected.

The archive, x64 DLL, actual version and hashes are checked. HTTPS acquisition and checksums are not independent publisher-signature verification. The upstream setup program is not executed.

## Recovery

Open **Backups** for the `installation.json` receipt and original files under **DLSS-Nexus-Backups**, beside the game executable. Backed-up add-ons use a non-loadable `.backup` suffix.

Handled installation failures trigger guarded rollback. Power loss, interruption or external changes can still require manual recovery.

1. Close the game.
2. Read the receipt's destinations, original hashes and backup paths.
3. Restore originals only when their hashes match the receipt.
4. Remove newly installed files only while they still match their recorded installed hashes.
5. Do not overwrite changes made by another mod or installer.

Uninstalling Nexus removes its app files and shortcuts, not game components, backups, library records or cached downloads.

[Third-party licenses and distribution terms](https://github.com/daemosofchaos/dlss-nexus/blob/main/THIRD-PARTY-NOTICES.md)
