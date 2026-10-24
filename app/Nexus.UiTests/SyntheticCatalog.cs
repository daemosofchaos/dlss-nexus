using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nexus.Core;

internal static class SyntheticCatalog
{
    // Deliberately inert fixture bytes. Public source tests do not need or redistribute deployment payloads.
    public static BundleCatalog Create()
    {
        var manifest = new BundleManifest { BundleVersion = "fixture", ConsumerSelections = new()
        {
            ["default"] = new() { PayloadPath = "renodx-dlss.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" },
            ["experimental"] = new() { PayloadPath = "experimental/renodx-dlss.addon64", TargetPath = "renodx-dlss.addon64", Family = "Controller" },
            ["legacy"] = new() { PayloadPath = "legacy/renodx-dlss5.addon64", TargetPath = "renodx-dlss5.addon64", Family = "Generic" },
            ["dx10"] = new() { PayloadPath = "dx10/renodx-dlss5.addon64", TargetPath = "renodx-dlss5.addon64", Family = "Generic" }
        } };
        var assets = new Dictionary<string, byte[]>
        {
            ["payload/dlss5-feed.addon64"] = Pe("fixture Feeder"),
            ["payload/dx10/dlss5-feed-helper.addon64"] = Pe("fixture Feeder helper"),
            ["payload/dx10/host64/dlss5-feed-host64.exe"] = Pe("fixture Feeder host"),
            ["payload/dx10/renodx-dlss5.addon64"] = Pe("NRGlobalTone\0 fixture DX10 Generic 7"),
            ["payload/renodx-dlss.addon64"] = Pe("RenoDX DLSS controller"),
            ["payload/experimental/renodx-dlss.addon64"] = Pe("RenoDX DLSS controller experimental"),
            ["payload/legacy/renodx-dlss5.addon64"] = Pe("NRGlobalTone\0"),
            ["payload/nvngx_dlss.dll"] = Pe("fixture SR"), ["payload/nvngx_dlssnr.dll"] = Pe("fixture NR"),
            ["payload/dlss5-feed.cfg"] = Encoding.UTF8.GetBytes("enabled=1\n"),
            ["payload/reshade-shaders/Shaders/DLSS5_Feed.fx"] = Encoding.UTF8.GetBytes("// inert fixture shader"),
            ["payload/reshade-shaders/Shaders/include/lumenite_Compute.fxh"] = Encoding.UTF8.GetBytes("// inert fixture include"),
            ["payload/reshade-shaders/Textures/lumenite_bluenoise256.png"] = [1, 2, 3],
            ["templates/" + InstallerEngine.PresetName] = Encoding.UTF8.GetBytes("Techniques=\n")
        };
        foreach (var asset in assets)
        {
            string path = asset.Key.StartsWith("payload/") ? asset.Key[8..] : asset.Key;
            manifest.Components.Add(new() { PayloadPath = path, TargetPath = path.StartsWith("templates/") ? InstallerEngine.PresetName : path,
                Sha256 = Convert.ToHexString(SHA256.HashData(asset.Value)) });
        }
        return new(manifest, key => new MemoryStream(assets[key], false));
    }
    public static byte[] Pe(string marker)
    {
        byte[] data = new byte[512]; data[0] = 0x4D; data[1] = 0x5A; BitConverter.GetBytes(128).CopyTo(data, 60);
        BitConverter.GetBytes(0x00004550u).CopyTo(data, 128); BitConverter.GetBytes((ushort)0x8664).CopyTo(data, 132);
        Encoding.ASCII.GetBytes(marker).CopyTo(data, 160); return data;
    }
}
