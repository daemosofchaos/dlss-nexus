using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Core;

public static class FileSafety
{
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Hash(stream);
    }
    public static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    public static string? ExistingHash(string path) => File.Exists(path) ? Hash(path) : null;
    public static bool IsInside(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Package paths must be relative.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, result) || result.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested file is outside the selected game folder.");
        EnsureNoLinks(result);
        return result;
    }
    public static void EnsureNoLinks(string path)
    {
        for (string? item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) &&
                (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked folders and files are not supported. Select the physical game folder.");
    }
    public static bool IsX64(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return IsX64(stream);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static bool IsX64(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) return false;
        stream.Position = offset;
        return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
    }
    public static bool ContainsMarker(string path, string marker)
    {
        using var stream = File.OpenRead(path);
        return ContainsMarker(stream, marker);
    }
    public static bool ContainsMarker(Stream stream, string marker)
    {
        stream.Position = 0;
        byte[] buffer = new byte[65536 + marker.Length];
        byte[] needle = Encoding.ASCII.GetBytes(marker);
        int carry = 0, read;
        while ((read = stream.Read(buffer, carry, 65536)) != 0)
        {
            int total = carry + read;
            if (buffer.AsSpan(0, total).IndexOf(needle) >= 0) return true;
            carry = Math.Min(marker.Length - 1, total);
            Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
        }
        return false;
    }
    public static bool IsReShade(string path) => IsX64(path) && ContainsMarker(path, "ReShade");
    public static string AddonFamily(string path)
    {
        using var stream = File.OpenRead(path);
        return AddonFamily(stream);
    }
    public static string AddonFamily(Stream stream)
    {
        if (!IsX64(stream)) throw new InvalidOperationException("Select a valid 64-bit RenoDX add-on (.addon64).");
        if (ContainsMarker(stream, "RenoDX DLSS controller")) return "Controller";
        if (ContainsMarker(stream, "NRGlobalTone\0")) return "Generic";
        throw new InvalidOperationException("This file is not a recognized ShortFuse or RenoDX Generic add-on.");
    }
    public static void EnsureGameClosed(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            process.Dispose();
            throw new InvalidOperationException("Close the game before installing. No installation can proceed while it is running.");
        }
    }
}