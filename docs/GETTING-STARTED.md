# Getting started

## Install Nexus

Run the Windows installer, then open **DLSS Nexus** from the Start Menu. For the portable version, extract the ZIP and open **DLSS-Nexus.exe**.

No separate .NET installation is needed.

**RTX 20, 30 and 40 series cards:** Nexus includes ShortFuse's patched DLSS Neural Rendering runtime to support these GeForce RTX generations.

For the included add-on, Feeder, DLSS runtime and shader versions, dates and checksums, see **[Bundled injection payloads](SAFETY.md#package-components)**. ReShade is downloaded when needed; an existing game runtime or a selected custom add-on may differ from the bundled versions.

> Close the game before setup. Do not use Nexus with anti-cheat. GPU and driver compatibility depends on the selected add-on and runtime.

## Add a game

1. Find your game in **All games**.
2. Select it and choose **Set up**.

Not listed? Use **Add game…** and choose its folder or executable. If several executables appear, select the game, not its launcher.

## Choose the setup

Answer **“Does this game have DLSS in its graphics settings?”**

| Answer | Setup |
|---|---|
| Yes | Uses the game's native DLSS. Includes shader files and an inactive Nexus preset; no Feeder add-on. |
| No | Adds Feeder support. Requires depth-buffer setup in ReShade. |

If unsure, check the game's settings first. A DLSS file in its folder is not enough to establish native support.

Leave **Advanced** closed unless you need a different add-on, an update or a DirectX override. If DirectX is unknown, select the version the game uses under **Advanced**.

### DirectX 10 (beta)

For a 64-bit DX10/DX10.1 game, choose **No** for native DLSS support. Nexus detects common DX10 imports automatically; if needed, choose **DirectX 10 - Feeder (beta)** under **Advanced**.

The Standard DX10 setup uses Feeder's helper and Generic v7.0.0-rc8, not the in-process ShortFuse build. The helper, its ReShade, neural add-on and runtimes go in `host64` beside the Feeder add-on. With a custom local ReShade add-on folder, `host64` goes there instead. The game's ReShade must not recursively load that folder.

Legacy v4.7 remains available but has known failures with newer NVIDIA drivers. Use the Standard DX10 build or an explicitly selected compatible Generic custom build. DX10 does not support the native-DLSS route.

### Review and install

ReShade is downloaded when missing; the first download needs internet access.

Nexus installs the bundled effects and includes into **reshade-shaders/Shaders**, textures into **reshade-shaders/Textures**, and **DLSS-Nexus.ini** beside the game executable. Existing custom local shader and texture folders are honored. The new native-DLSS preset starts with effects disabled; the Feeder preset enables the matched Lumenite Kernel and DLSS5 Feed effects.

To repair an incomplete setup, close the game and run **Set up → Review → Install** again. Missing shader files and the Nexus preset will be installed. Missing or default search paths are repaired with backups, while other ReShade settings and existing preset selections are kept.

Choose **Review**. Check the game and the Install/Replace/Keep summary, then choose **Install**. Use **File details** for individual paths.

If Install is unavailable, the **Installation blocked** panel explains what needs attention. Advisory warnings are shown separately.

### Switch from Feeder to native DLSS

1. Choose **Yes — use the game's DLSS** and the game's DirectX 11 or 12 mode.
2. Under **Advanced**, enable **Replace existing add-ons** to back up and remove the previous Feeder and neural add-ons.
3. Choose **Review**. If Feeder is enabled in the active preset, choose **Turn off Feeder for this setup**.
4. Review the preset change, then choose **Install**. Nexus backs up the original preset and removes only its Feeder technique entries. Your other effects, preset selection and settings are retained.

The Feeder-off action prepares the review; no game files change until you confirm **Install**. You can also disable **DLSS5_Feed** in ReShade yourself, close the game and choose **Recheck**.

## Finish in the game

Use **Launch game** on the **Ready** step to start the executable you selected for setup. Nexus stays open; launching does not reinstall components or mark any feature enabled.

Games that require launcher sign-in, special startup arguments or Steam/Epic launch options should be started through that launcher instead.

### Native DLSS

1. Start the game and enable DLSS in its graphics settings.
2. Open ReShade with **Home** and open the ShortFuse panel. Use **DLSS-Nexus.ini** for a new setup, or keep your active preset after switching from Feeder. Leave Feeder effects off for native DLSS.
3. Enable Neural Rendering and check its activity during gameplay.

### Feeder

1. Start the game and open ReShade with **Home**.
2. Select **DLSS-Nexus.ini**.
3. In **Generic Depth**, select the game's scene-depth buffer. This varies by game; use instructions for that game rather than guessing.
4. In the Standard ShortFuse panel, select the **Present** hook and check neural activity. Custom and Legacy builds may use different controls.

An existing ReShade setup may use a different overlay key. “Installed” confirms files, not active neural rendering or gameplay compatibility.

### DX10 Feeder helper

Follow the Feeder steps above to select **DLSS-Nexus.ini** and the correct scene-depth buffer. Then open **DLSS 5 Feed** and choose **Show the DLSS 5 panel in-game** while using windowed or borderless mode. Enable Neural Rendering and check activity in the helper's **RenoDX Generic** panel; do not look for a ShortFuse Present hook in the game's panel.

DX10 uses native-resolution DLAA. The helper's **Show as texture** panel mode is not available on DX10. For a separate helper window, use `host_window=1` in the Feeder configuration.

## Track settings

Return to **Games → Tracked** and select the game.

- **Check status** refreshes component versions and preset status.
- **Launch game** starts the recorded executable. If it has not been identified yet, use **Check status** or **Set up** first.
- **Record in-game settings** saves your Enabled/Disabled report for each feature.
- **Recheck needed** means component or known ReShade configuration files changed since your report.

Reports are local records, not live telemetry. Verify the feature in the game's settings or add-on panel before marking it enabled.

## Need help?

Use **Backups** after installation for original files and the recovery receipt. Uninstalling Nexus leaves game components, backups, the saved library and the ReShade cache intact.

[Compatibility, security and recovery →](SAFETY.md)
