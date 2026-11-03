using System.Buffers.Binary;

namespace StampCut.Core.FFmpeg;

public static class ImageInfo
{
    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Размер PNG из заголовка IHDR без декодирования.</summary>
    public static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[24];
            if (fs.Read(buf) < 24) return false;
            if (!buf[..8].SequenceEqual(PngSignature)) return false;
            if (buf[12] != 'I' || buf[13] != 'H' || buf[14] != 'D' || buf[15] != 'R') return false;
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(buf[16..20]);
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(buf[20..24]);
            return width > 0 && height > 0;
        }
        catch { return false; }
    }
}
