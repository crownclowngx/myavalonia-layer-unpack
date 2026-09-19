using System.Text;

namespace LayerUnpackPlugin.Headless.Infrastructure;

public enum ArchiveKind { Unknown, Zip, SevenZip, Rar, Tar, GZip, BZip2, Xz }

/// <summary>仅查看固定长度签名，不读取目录或尝试密码。达到深度时也不会打开内部归档。</summary>
public static class ArchiveProbe
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz" };

    public static bool HasKnownExtension(string path) => Extensions.Contains(Path.GetExtension(path));

    public static async Task<ArchiveKind> DetectAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bytes = new byte[512];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken).ConfigureAwait(false);
        return Detect(bytes.AsSpan(0, count), path);
    }

    public static ArchiveKind Detect(ReadOnlySpan<byte> bytes, string path = "")
    {
        if (bytes.StartsWith("PK\x03\x04"u8) || bytes.StartsWith("PK\x05\x06"u8) || bytes.StartsWith("PK\x07\x08"u8)) return ArchiveKind.Zip;
        if (bytes.StartsWith(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c })) return ArchiveKind.SevenZip;
        if (bytes.StartsWith("Rar!\x1a\x07"u8)) return ArchiveKind.Rar;
        if (bytes.StartsWith(new byte[] { 0x1f, 0x8b })) return ArchiveKind.GZip;
        if (bytes.StartsWith("BZh"u8)) return ArchiveKind.BZip2;
        if (bytes.StartsWith(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00 })) return ArchiveKind.Xz;
        if (bytes.Length == 512)
        {
            var checksumText = Encoding.ASCII.GetString(bytes.Slice(148, 8)).Trim('\0', ' ');
            try
            {
                var expected = Convert.ToInt32(checksumText, 8);
                var actual = 256;
                for (var i = 0; i < 512; i++) if (i < 148 || i >= 156) actual += bytes[i];
                if (actual == expected) return ArchiveKind.Tar;
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { }
            if (bytes.IndexOfAnyExcept((byte)0) < 0 && Path.GetExtension(path).Equals(".tar", StringComparison.OrdinalIgnoreCase))
                return ArchiveKind.Tar;
        }
        return ArchiveKind.Unknown;
    }

    public static string OutputName(string source)
    {
        var name = Path.GetFileName(source);
        if (Application.ArchiveSourceResolver.IsSplitPath(source)) name = Path.GetFileNameWithoutExtension(name);
        foreach (var ending in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
            if (name.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) return name[..^ending.Length];
        return Path.GetFileNameWithoutExtension(name);
    }
}
