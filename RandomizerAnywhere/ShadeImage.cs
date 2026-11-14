using System.Collections.Concurrent;
using System.IO.Compression;

namespace RandomizerAnywhere;

// The dark layer under the HUD boxes, as a PNG in exactly the box's size: a rounded rectangle
// with an anti-aliased edge that sits just inside the game's rounded NavButton frame.
// Manialink bgcolor quads are always square-cornered; stacked or cut rectangles showed corners,
// steps or rings. One image per box size has none of that and is never stretched out of shape.
// Served by ReplayServer at /shade/<w>x<h>a<alpha>.png, sizes in tenths of a manialink unit.
internal static class ShadeImage
{
    private const int PixelsPerUnit = 12;
    private const double Inset = 0.2;
    private const double Radius = 0.6;

    // box sizes repeat (a handful of widgets, tiles and windows), so the cache stays small; the
    // cap only guards against someone requesting every possible size
    private const int MaxCached = 400;
    private const int MaxTenths = 1300;

    private static readonly ConcurrentDictionary<(int W, int H, int A), byte[]> Cache = new();

    public static string FileName(double width, double height, int alpha) =>
        $"{(int)Math.Round(width * 10)}x{(int)Math.Round(height * 10)}a{alpha}.png";

    public static byte[]? TryGet(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(\d{1,4})x(\d{1,4})a(\d{1,2})\.png$");
        if (!match.Success)
        {
            return null;
        }

        var w = int.Parse(match.Groups[1].Value);
        var h = int.Parse(match.Groups[2].Value);
        var a = int.Parse(match.Groups[3].Value);
        if (w is < 10 or > MaxTenths || h is < 10 or > MaxTenths || a is < 1 or > 15)
        {
            return null;
        }

        if (Cache.TryGetValue((w, h, a), out var cached))
        {
            return cached;
        }

        var png = Render(w / 10.0, h / 10.0, a);
        if (Cache.Count < MaxCached)
        {
            Cache[(w, h, a)] = png;
        }

        return png;
    }

    private static byte[] Render(double width, double height, int alpha)
    {
        var pw = Math.Max(1, (int)Math.Round(width * PixelsPerUnit));
        var ph = Math.Max(1, (int)Math.Round(height * PixelsPerUnit));
        var maxAlpha = alpha * 17; // same darkness as bgcolor="000<alpha>"

        var halfW = width / 2 - Inset;
        var halfH = height / 2 - Inset;
        var radius = Math.Min(Radius, Math.Min(halfW, halfH));

        // one filter byte per row, then RGBA
        var raw = new byte[ph * (1 + pw * 4)];
        for (var y = 0; y < ph; y++)
        {
            var row = y * (1 + pw * 4);
            var py = (y + 0.5) / PixelsPerUnit - height / 2;
            for (var x = 0; x < pw; x++)
            {
                var px = (x + 0.5) / PixelsPerUnit - width / 2;

                // signed distance to the rounded rectangle, in units
                var qx = Math.Abs(px) - (halfW - radius);
                var qy = Math.Abs(py) - (halfH - radius);
                var outside = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2));
                var distance = outside + Math.Min(Math.Max(qx, qy), 0) - radius;

                // one pixel of anti-aliasing at the edge
                var coverage = Math.Clamp(0.5 - distance * PixelsPerUnit, 0, 1);
                raw[row + 1 + x * 4 + 3] = (byte)Math.Round(coverage * maxAlpha);
            }
        }

        return EncodePng(pw, ph, raw);
    }

    private static byte[] EncodePng(int width, int height, byte[] raw)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var buffer = new byte[4];
        WriteBigEndian(buffer, 0, (uint)data.Length);
        output.Write(buffer);

        var typeAndData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            typeAndData[i] = (byte)type[i];
        }

        data.CopyTo(typeAndData, 4);
        output.Write(typeAndData);

        WriteBigEndian(buffer, 0, Crc32(typeAndData));
        output.Write(buffer);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
