using System.IO.Compression;

namespace Sovereign.Desktop.Phone;

/// <summary>Byte-mode QR, error correction M, versions 1–10. Drawn on this PC. No QR website.</summary>
public static class QrCode
{
    public static byte[] Png(string text)
    {
        var modules = Modules(text);
        const int scale = 8;
        const int quiet = 4;
        var size = (modules.GetLength(0) + quiet * 2) * scale;
        var raw = new byte[size * size];
        for (var y = 0; y < modules.GetLength(0); y++)
            for (var x = 0; x < modules.GetLength(1); x++)
            {
                if (!modules[y, x]) continue;
                for (var dy = 0; dy < scale; dy++)
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var py = (y + quiet) * scale + dy;
                        var px = (x + quiet) * scale + dx;
                        raw[py * size + px] = 1;
                    }
            }
        return EncodePng(raw, size);
    }

    internal static bool[,] Modules(string text) => Draw(text, null);

    private static bool[,] Draw(string text, int? forcedMask)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var version = ChooseVersion(bytes.Length);
        var size = version * 4 + 17;
        var matrix = new bool[size, size];
        var reserved = new bool[size, size];
        PlaceFunctionPatterns(version, matrix, reserved);
        var data = Interleave(version, Pack(version, bytes));
        DrawData(matrix, reserved, data);
        var mask = forcedMask ?? ChooseMask(matrix, reserved);
        ApplyMask(matrix, reserved, mask);
        PlaceFormat(matrix, mask);
        if (version >= 7) PlaceVersion(matrix, version);
        return matrix;
    }

    private static int ChooseVersion(int length)
    {
        for (var version = 1; version <= Capacities.Length; version++)
        {
            var bits = 4 + (version <= 9 ? 8 : 16) + length * 8;
            if ((bits + 7) / 8 <= DataCodewords(version)) return version;
        }
        throw new ArgumentException("That address is too long for the pairing code.");
    }

    private static byte[] Pack(int version, byte[] payload)
    {
        var countBits = version <= 9 ? 8 : 16;
        var bits = new List<bool> { false, true, false, false };
        for (var i = countBits - 1; i >= 0; i--) bits.Add(((payload.Length >> i) & 1) == 1);
        foreach (var value in payload)
            for (var i = 7; i >= 0; i--) bits.Add(((value >> i) & 1) == 1);
        var capacity = DataCodewords(version) * 8;
        for (var i = 0; i < 4 && bits.Count < capacity; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);
        var data = new byte[DataCodewords(version)];
        for (var i = 0; i < bits.Count / 8 && i < data.Length; i++)
        {
            byte value = 0;
            for (var bit = 0; bit < 8; bit++) if (bits[i * 8 + bit]) value |= (byte)(1 << (7 - bit));
            data[i] = value;
        }
        var pad = new byte[] { 0xEC, 0x11 };
        for (var i = bits.Count / 8; i < data.Length; i++) data[i] = pad[(i - bits.Count / 8) % 2];
        return data;
    }

    private static byte[] Interleave(int version, byte[] data)
    {
        var spec = Specs[version - 1];
        var blocks = new List<byte[]>();
        var offset = 0;
        void Take(int count, int length)
        {
            for (var i = 0; i < count; i++)
            {
                var block = new byte[length];
                Array.Copy(data, offset, block, 0, length);
                offset += length;
                blocks.Add(block);
            }
        }
        Take(spec.Group1, spec.Data1);
        Take(spec.Group2, spec.Data2);
        var ecc = blocks.Select(block => ReedSolomon(block, spec.Ecc)).ToArray();
        var output = new List<byte>();
        var longest = Math.Max(spec.Data1, spec.Data2);
        for (var i = 0; i < longest; i++)
            foreach (var block in blocks)
                if (i < block.Length) output.Add(block[i]);
        for (var i = 0; i < spec.Ecc; i++)
            foreach (var block in ecc) output.Add(block[i]);
        return output.ToArray();
    }

    private static void DrawData(bool[,] matrix, bool[,] reserved, byte[] data)
    {
        var size = matrix.GetLength(0);
        var bit = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
            {
                for (var shift = 0; shift < 2; shift++)
                {
                    var x = right - shift;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    if (reserved[y, x]) continue;
                    var on = bit < data.Length * 8 && ((data[bit / 8] >> (7 - bit % 8)) & 1) == 1;
                    matrix[y, x] = on;
                    bit++;
                }
            }
        }
    }

    private static int ChooseMask(bool[,] matrix, bool[,] reserved)
    {
        var best = 0;
        var bestScore = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            var trial = (bool[,])matrix.Clone();
            ApplyMask(trial, reserved, mask);
            var score = Penalty(trial);
            if (score < bestScore) { bestScore = score; best = mask; }
        }
        return best;
    }

    private static void ApplyMask(bool[,] matrix, bool[,] reserved, int mask)
    {
        var size = matrix.GetLength(0);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (reserved[y, x]) continue;
                var flip = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                };
                if (flip) matrix[y, x] = !matrix[y, x];
            }
    }

    private static int Penalty(bool[,] matrix)
    {
        var size = matrix.GetLength(0);
        var score = 0;
        for (var y = 0; y < size; y++) score += RunPenalty(Row(matrix, y));
        for (var x = 0; x < size; x++) score += RunPenalty(Column(matrix, x));
        for (var y = 0; y < size - 1; y++)
            for (var x = 0; x < size - 1; x++)
            {
                var bit = matrix[y, x];
                if (bit == matrix[y, x + 1] && bit == matrix[y + 1, x] && bit == matrix[y + 1, x + 1]) score += 3;
            }
        var dark = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++) if (matrix[y, x]) dark++;
        score += Math.Abs(dark * 100 / (size * size) - 50) / 5 * 10;
        return score;
    }

    private static int RunPenalty(bool[] line)
    {
        var score = 0;
        var run = 1;
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == line[i - 1]) run++;
            else { if (run >= 5) score += run - 2; run = 1; }
        }
        if (run >= 5) score += run - 2;
        for (var i = 0; i <= line.Length - 7; i++)
        {
            if (line[i] && !line[i + 1] && line[i + 2] && line[i + 3] && line[i + 4] && !line[i + 5] && line[i + 6])
                score += 40;
        }
        return score;
    }

    private static bool[] Row(bool[,] matrix, int y)
    {
        var line = new bool[matrix.GetLength(1)];
        for (var x = 0; x < line.Length; x++) line[x] = matrix[y, x];
        return line;
    }

    private static bool[] Column(bool[,] matrix, int x)
    {
        var line = new bool[matrix.GetLength(0)];
        for (var y = 0; y < line.Length; y++) line[y] = matrix[y, x];
        return line;
    }

    private static void PlaceFunctionPatterns(int version, bool[,] matrix, bool[,] reserved)
    {
        var size = matrix.GetLength(0);
        void Finder(int ox, int oy)
        {
            for (var y = -1; y <= 7; y++)
                for (var x = -1; x <= 7; x++)
                {
                    var py = oy + y;
                    var px = ox + x;
                    if (py < 0 || px < 0 || py >= size || px >= size) continue;
                    var on = x is >= 0 and <= 6 && y is >= 0 and <= 6 && (x == 0 || x == 6 || y == 0 || y == 6 || x is >= 2 and <= 4 && y is >= 2 and <= 4);
                    matrix[py, px] = on;
                    reserved[py, px] = true;
                }
        }
        Finder(0, 0);
        Finder(size - 7, 0);
        Finder(0, size - 7);
        for (var i = 0; i < size; i++)
        {
            if (!reserved[6, i]) { matrix[6, i] = i % 2 == 0; reserved[6, i] = true; }
            if (!reserved[i, 6]) { matrix[i, 6] = i % 2 == 0; reserved[i, 6] = true; }
        }
        var centers = Alignments[version - 1];
        for (var iy = 0; iy < centers.Length; iy++)
            for (var ix = 0; ix < centers.Length; ix++)
            {
                // Skip only the three finder overlaps. Alignment patterns on
                // timing row/column 6 are required from version 7 onward.
                if (iy == 0 && (ix == 0 || ix == centers.Length - 1) || iy == centers.Length - 1 && ix == 0) continue;
                var cy = centers[iy]; var cx = centers[ix];
                for (var y = -2; y <= 2; y++)
                    for (var x = -2; x <= 2; x++)
                    {
                        matrix[cy + y, cx + x] = Math.Max(Math.Abs(x), Math.Abs(y)) != 1;
                        reserved[cy + y, cx + x] = true;
                    }
            }
        matrix[size - 8, 8] = true;
        reserved[size - 8, 8] = true;
        for (var i = 0; i < 9; i++) { reserved[8, i] = true; reserved[i, 8] = true; }
        for (var i = 0; i < 8; i++) { reserved[8, size - 1 - i] = true; reserved[size - 1 - i, 8] = true; }
        if (version >= 7)
            for (var i = 0; i < 6; i++)
                for (var j = 0; j < 3; j++)
                {
                    reserved[i, size - 11 + j] = true;
                    reserved[size - 11 + j, i] = true;
                }
    }

    private static void PlaceFormat(bool[,] matrix, int mask)
    {
        var bits = FormatBits(0b00, mask);
        var size = matrix.GetLength(0);
        bool Bit(int index) => ((bits >> index) & 1) == 1;
        void At(int column, int row, int index) => matrix[row, column] = Bit(index);
        for (var i = 0; i <= 5; i++) At(8, i, i);
        At(8, 7, 6);
        At(8, 8, 7);
        At(7, 8, 8);
        for (var i = 9; i < 15; i++) At(14 - i, 8, i);
        for (var i = 0; i < 8; i++) At(size - 1 - i, 8, i);
        for (var i = 8; i < 15; i++) At(8, size - 15 + i, i);
        matrix[size - 8, 8] = true;
    }

    private static void PlaceVersion(bool[,] matrix, int version)
    {
        var bits = VersionBits(version);
        var size = matrix.GetLength(0);
        for (var i = 0; i < 18; i++)
        {
            var on = ((bits >> i) & 1) == 1;
            var row = i / 3;
            var col = size - 11 + i % 3;
            matrix[row, col] = on;
            matrix[col, row] = on;
        }
    }

    private static int FormatBits(int ecc, int mask)
    {
        var data = (ecc << 3) | mask;
        var rem = data << 10;
        for (var i = 14; i >= 10; i--)
            if (((rem >> i) & 1) != 0) rem ^= 0x537 << (i - 10);
        return ((data << 10) | (rem & 0x3FF)) ^ 0x5412;
    }

    private static int VersionBits(int version)
    {
        var rem = version << 12;
        for (var i = 17; i >= 12; i--)
            if (((rem >> i) & 1) != 0) rem ^= 0x1F25 << (i - 12);
        return (version << 12) | (rem & 0xFFF);
    }

    private static byte[] ReedSolomon(byte[] data, int ecc)
    {
        var divisor = new byte[ecc];
        divisor[ecc - 1] = 1;
        var root = 1;
        for (var i = 0; i < ecc; i++)
        {
            for (var j = 0; j < divisor.Length; j++)
            {
                divisor[j] = Multiply(divisor[j], (byte)root);
                if (j + 1 < divisor.Length) divisor[j] ^= divisor[j + 1];
            }
            root = Multiply((byte)root, 0x02);
        }
        var result = new byte[ecc];
        foreach (var value in data)
        {
            var factor = (byte)(result[0] ^ value);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(byte leftByte, byte rightByte)
    {
        var left = (int)leftByte;
        var right = (int)rightByte;
        var result = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((right & 1) != 0) result ^= left;
            var high = (left & 0x80) != 0;
            left = (left << 1) & 0xFF;
            if (high) left ^= 0x11D;
            right >>= 1;
        }
        return (byte)result;
    }

    private static int DataCodewords(int version)
    {
        var spec = Specs[version - 1];
        return spec.Group1 * spec.Data1 + spec.Group2 * spec.Data2;
    }

    private static byte[] EncodePng(byte[] pixels, int size)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(output, "IHDR", Writer(chunk =>
        {
            chunk.Write(Int(size));
            chunk.Write(Int(size));
            chunk.WriteByte(8);
            chunk.WriteByte(0);
            chunk.WriteByte(0);
            chunk.WriteByte(0);
            chunk.WriteByte(0);
        }));
        var scan = new byte[size * (size + 1)];
        for (var y = 0; y < size; y++)
        {
            scan[y * (size + 1)] = 0;
            for (var x = 0; x < size; x++) scan[y * (size + 1) + 1 + x] = pixels[y * size + x] == 1 ? (byte)0 : (byte)255;
        }
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, true)) deflate.Write(scan);
        var checksum = Adler32(scan);
        compressed.Write(Int((int)checksum));
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        output.Write(Int(data.Length));
        var header = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(header);
        output.Write(data);
        var crcSource = header.Concat(data).ToArray();
        output.Write(Int((int)Crc32(crcSource)));
    }

    private static byte[] Writer(Action<MemoryStream> fill)
    {
        using var stream = new MemoryStream();
        fill(stream);
        return stream.ToArray();
    }

    private static byte[] Int(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private readonly record struct BlockSpec(int Ecc, int Group1, int Data1, int Group2, int Data2);

    private static readonly BlockSpec[] Specs =
    [
        new(10, 1, 16, 0, 0),
        new(16, 1, 28, 0, 0),
        new(26, 1, 44, 0, 0),
        new(18, 2, 32, 0, 0),
        new(24, 2, 43, 0, 0),
        new(16, 4, 27, 0, 0),
        new(18, 4, 31, 0, 0),
        new(22, 2, 38, 2, 39),
        new(22, 3, 36, 2, 37),
        new(26, 4, 43, 1, 44)
    ];

    private static readonly int[] Capacities = [14, 26, 42, 62, 84, 106, 122, 152, 180, 213];

    private static readonly int[][] Alignments =
    [
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50]
    ];
}
