using System.Text;

namespace Nexus.Core;

public enum GraphicsApi { Unknown, Direct3D11, Direct3D12, Unsupported, Direct3D10 }

// Read actual PE import descriptors rather than guessing from strings in game assets.
// Static imports are a hint, not proof of the API selected at runtime.
public static class GraphicsApiDetection
{
    public static GraphicsApi Detect(string executable)
    {
        try
        {
            var imports = Imports(executable);
            bool d10 = imports.Overlaps(["d3d10.dll", "d3d10_1.dll"]);
            bool d11 = imports.Contains("d3d11.dll"), d12 = imports.Contains("d3d12.dll");
            if (d10 && !d11 && !d12) return GraphicsApi.Direct3D10;
            if (d11 && !d10 && !d12) return GraphicsApi.Direct3D11;
            if (d12 && !d10 && !d11) return GraphicsApi.Direct3D12;
            if (!d10 && !d11 && !d12 && imports.Overlaps(["d3d9.dll", "opengl32.dll", "vulkan-1.dll"])) return GraphicsApi.Unsupported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException) { }
        return GraphicsApi.Unknown;
    }

    public static HashSet<string> Imports(string path)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input, Encoding.ASCII);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (!FileSafety.IsX64(input)) return names;
        input.Position = 0x3c; int pe = reader.ReadInt32();
        input.Position = pe + 6; int sections = reader.ReadUInt16();
        input.Position = pe + 20; int optionalSize = reader.ReadUInt16();
        int optional = pe + 24;
        if (sections > 96 || optionalSize < 128 || optional + optionalSize + sections * 40L > input.Length) return names;
        input.Position = optional; if (reader.ReadUInt16() != 0x20b) return names;
        input.Position = optional + 108; if (reader.ReadUInt32() < 2) return names;
        input.Position = optional + 120; uint importsRva = reader.ReadUInt32();
        List<(uint Rva, uint Size, uint Offset)> ranges = [];
        for (int i = 0; i < sections; i++)
        {
            input.Position = optional + optionalSize + i * 40 + 12;
            uint rva = reader.ReadUInt32(), size = reader.ReadUInt32(), offset = reader.ReadUInt32();
            ranges.Add((rva, size, offset));
        }
        long Offset(uint rva)
        {
            foreach (var range in ranges)
                if (rva >= range.Rva && rva - range.Rva < range.Size) return range.Offset + (long)(rva - range.Rva);
            return -1;
        }
        long table = Offset(importsRva);
        if (table < 0) return names;
        for (int i = 0; i < 512; i++)
        {
            long descriptor = table + i * 20;
            if (descriptor + 20 > input.Length) break;
            input.Position = descriptor + 12; uint nameRva = reader.ReadUInt32();
            if (nameRva == 0) break;
            long nameOffset = Offset(nameRva);
            if (nameOffset < 0 || nameOffset >= input.Length) break;
            input.Position = nameOffset;
            List<byte> text = [];
            for (int n = 0; n < 260 && input.Position < input.Length; n++)
            { byte c = reader.ReadByte(); if (c == 0) break; text.Add(c); }
            names.Add(Encoding.ASCII.GetString(text.ToArray()));
        }
        return names;
    }
}