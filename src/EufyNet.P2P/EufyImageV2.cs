namespace EufyNet.P2P;

/// <summary>
/// <c>v2_eufysecurity:&lt;serial&gt;:&lt;code&gt;:</c> pictures (http/decodeImageV2.js). Only the JPEG head is scrambled: the
/// quantization tables, the frame size and the luma tables. From the chroma DC Huffman table on, the file is a plain
/// baseline JPEG. The head is rebuilt from a standard template (so the tones are close, not exact).
/// <para>Node guesses the size by trial-decoding with jpeg-js. Here the scan is Huffman-decoded instead, the 8×8 blocks are
/// counted until EOI, and the size whose block count matches is taken.</para>
/// </summary>
public static class EufyImageV2
{
    public const string Prefix = "v2_eufysecurity:";

    private static readonly byte[] DcChroma = [0xFF, 0xC4, 0x00, 0x1F, 0x01];

    /// <summary>SOS with three components (Y, Cb, Cr).</summary>
    private static readonly byte[] StartOfScan = [0xFF, 0xDA, 0x00, 0x0C, 0x03];

    // DHT DC + AC chroma: the standard tables (JPEG Annex K.3).
    private static readonly byte[] ChromaTables = Convert.FromHexString(
        "ffc4001f0100030101010101010101010000000000000102030405060708090a0b"
        + "ffc400b51100020102040403040705040400010277000102031104052131061241510761711322328108144291a1b1c109233352f0156272d10a162434e125f11718191a262728292a35363738393a434445464748494a535455565758595a636465666768696a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9fa");

    // SOI, APP0, DQT luma + chroma, SOF0 (size and luma sampling patched in), DHT DC + AC luma: standard tables.
    private static readonly byte[] Template = Convert.FromHexString(
        "ffd8ffe000104a46494600010100000100010000ffdb0043000503040404030504040405050506070c08070707070f0b0b090c110f1212110f111113161c1713141a1511111821181a1d1d1f1f1f13172224221e241c1e1f1effdb0043010505050706070e08080e1e1411141e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1effc00011080101010103012200021101031101ffc4001f0000010501010101010100000000000000000102030405060708090a0bffc400b5100002010303020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f02433627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9fa");

    private const int OffHeight = 163;
    private const int OffWidth = 165;
    private const int OffYSampling = 169;

    /// <summary>Common picture sizes (decodeImageV2.js SIZE_LADDER); a block count matching one of them wins over other factorizations.</summary>
    private static readonly (int Width, int Height)[] Sizes =
    [
        (160, 90), (240, 135), (256, 144), (320, 180), (384, 216), (400, 225), (480, 270), (512, 288), (576, 324), (640, 360),
        (704, 396), (768, 432), (848, 480), (960, 540), (1024, 576), (1280, 720), (1600, 900), (1920, 1080), (2560, 1440),
        (176, 144), (320, 240), (352, 288), (480, 360), (640, 480), (800, 600), (1024, 768), (1280, 960),
    ];

    /// <summary>Luma sampling byte, luma blocks per MCU, MCU width and height; tried in this order.</summary>
    private static readonly (byte Sampling, int LumaBlocks, int McuWidth, int McuHeight)[] Subsamplings =
    [
        (0x22, 4, 16, 16), // 4:2:0
        (0x21, 2, 16, 8), //  4:2:2
        (0x11, 1, 8, 8), //   4:4:4
    ];

    public static bool IsV2(ReadOnlySpan<byte> data) =>
        data.Length > Prefix.Length && data[..Prefix.Length].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(Prefix));

    /// <summary>A JPEG, or null when the data does not have the expected structure.</summary>
    public static byte[]? Rebuild(byte[] data)
    {
        byte[] tail;
        var start = data.AsSpan().IndexOf(DcChroma);
        if (start >= 0)
        {
            tail = data[start..];
        }
        else
        {
            // HomeBase 2 3.4.2.6h scrambles the chroma tables too; the visible end of them is the standard table.
            var scan = data.AsSpan().IndexOf(StartOfScan);
            if (scan < 0)
                return null;
            tail = [.. ChromaTables, .. data[scan..]];
        }
        foreach (var (sampling, lumaBlocks, mcuWidth, mcuHeight) in Subsamplings)
        {
            var probe = Build(tail, 16, 16, sampling);
            if (CountMcus(probe, lumaBlocks) is not { } mcus)
                continue;
            var (width, height) = SizeFor(mcus, mcuWidth, mcuHeight);
            return Build(tail, width, height, sampling);
        }
        return null;
    }

    private static byte[] Build(byte[] tail, int width, int height, byte sampling)
    {
        var jpeg = new byte[Template.Length + tail.Length];
        Template.CopyTo(jpeg, 0);
        jpeg[OffHeight] = (byte)(height >> 8);
        jpeg[OffHeight + 1] = (byte)height;
        jpeg[OffWidth] = (byte)(width >> 8);
        jpeg[OffWidth + 1] = (byte)width;
        jpeg[OffYSampling] = sampling;
        tail.CopyTo(jpeg, Template.Length);
        return jpeg;
    }

    internal static (int Width, int Height) SizeFor(int mcus, int mcuWidth, int mcuHeight)
    {
        foreach (var (w, h) in Sizes)
        {
            if (Ceiling(w, mcuWidth) * Ceiling(h, mcuHeight) == mcus)
                return (w, h);
        }
        // Otherwise the factorization closest to 16:9.
        var best = (Width: mcus * mcuWidth, Height: mcuHeight);
        for (var columns = 1; columns <= mcus; columns++)
        {
            if (mcus % columns != 0)
                continue;
            var candidate = (Width: columns * mcuWidth, Height: mcus / columns * mcuHeight);
            if (Math.Abs(Math.Log((double)candidate.Width / candidate.Height / (16.0 / 9))) < Math.Abs(Math.Log((double)best.Width / best.Height / (16.0 / 9))))
                best = candidate;
        }
        return best;
    }

    private static int Ceiling(int value, int unit) => (value + unit - 1) / unit;

    /// <summary>
    /// Decodes the scan of a baseline YCbCr JPEG with the given luma blocks per MCU and counts whole MCUs until EOI. Null when
    /// the Huffman data does not fit that layout (a bad code, a block longer than 64, or leftovers at EOI).
    /// </summary>
    internal static int? CountMcus(byte[] jpeg, int lumaBlocks)
    {
        var tables = new Dictionary<int, Huffman>(); // class << 4 | id
        int[]? componentTables = null; // per scan component: dc id << 4 | ac id
        var pos = 2;
        while (pos + 4 <= jpeg.Length && componentTables is null)
        {
            if (jpeg[pos] != 0xFF)
                return null;
            var marker = jpeg[pos + 1];
            var length = jpeg[pos + 2] << 8 | jpeg[pos + 3];
            var segment = jpeg.AsSpan(pos + 4, Math.Min(length - 2, jpeg.Length - pos - 4));
            switch (marker)
            {
                case 0xC4:
                    for (var i = 0; i + 17 <= segment.Length;)
                    {
                        var counts = segment.Slice(i + 1, 16).ToArray();
                        var total = counts.Sum(c => c);
                        if (i + 17 + total > segment.Length)
                            return null;
                        tables[segment[i]] = new Huffman(counts, segment.Slice(i + 17, total).ToArray());
                        i += 17 + total;
                    }
                    break;
                case 0xDD: // restart interval: RST markers are skipped below
                    break;
                case 0xDA:
                    if (segment.Length < 7 || segment[0] != 3)
                        return null;
                    componentTables = [segment[2], segment[4], segment[6]];
                    break;
            }
            pos += 2 + length;
        }
        if (componentTables is null)
            return null;

        var reader = new BitReader(jpeg, pos);
        var blockTables = Enumerable.Repeat(componentTables[0], lumaBlocks).Append(componentTables[1]).Append(componentTables[2]).ToArray();
        var mcus = 0;
        while (true)
        {
            for (var block = 0; block < blockTables.Length; block++)
            {
                var t = blockTables[block];
                if (!tables.TryGetValue(t >> 4, out var dc) || !tables.TryGetValue(0x10 | (t & 0xF), out var ac))
                    return null;
                switch (DecodeBlock(reader, dc, ac))
                {
                    case BlockResult.End:
                        // EOI may only come between MCUs, after at most 7 padding bits.
                        return block == 0 && reader.BitsSinceMcu < 8 && mcus > 0 ? mcus : null;
                    case BlockResult.Invalid:
                        return null;
                }
            }
            mcus++;
            reader.MarkMcu();
        }
    }

    private enum BlockResult { Ok, End, Invalid }

    private static BlockResult DecodeBlock(BitReader reader, Huffman dc, Huffman ac)
    {
        var size = dc.Decode(reader);
        if (size < 0)
            return reader.Ended ? BlockResult.End : BlockResult.Invalid;
        if (size > 11 || !reader.Skip(size))
            return reader.Ended ? BlockResult.End : BlockResult.Invalid;
        for (var k = 1; k < 64;)
        {
            var rs = ac.Decode(reader);
            if (rs < 0)
                return reader.Ended ? BlockResult.End : BlockResult.Invalid;
            int run = rs >> 4, bits = rs & 0xF;
            if (bits == 0)
            {
                if (run == 15)
                {
                    k += 16;
                    continue;
                }
                break; // EOB
            }
            k += run;
            if (k > 63 || bits > 10 || !reader.Skip(bits))
                return reader.Ended ? BlockResult.End : BlockResult.Invalid;
            k++;
        }
        return BlockResult.Ok;
    }

    /// <summary>Canonical Huffman table (JPEG Annex C).</summary>
    private sealed class Huffman
    {
        private readonly int[] _maxCode = new int[18];
        private readonly int[] _valPtr = new int[17];
        private readonly int[] _minCode = new int[17];
        private readonly byte[] _values;

        public Huffman(byte[] counts, byte[] values)
        {
            _values = values;
            int code = 0, k = 0;
            for (var length = 1; length <= 16; length++)
            {
                _valPtr[length] = k;
                _minCode[length] = code;
                code += counts[length - 1];
                k += counts[length - 1];
                _maxCode[length] = counts[length - 1] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            _maxCode[17] = int.MaxValue;
        }

        /// <summary>The symbol, or -1 for no code (bad data or end of the scan).</summary>
        public int Decode(BitReader reader)
        {
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                var bit = reader.Bit();
                if (bit < 0)
                    return -1;
                code = code << 1 | bit;
                if (code <= _maxCode[length])
                    return _values[_valPtr[length] + code - _minCode[length]];
            }
            return -1;
        }
    }

    /// <summary>Entropy-coded bits: 0xFF00 is 0xFF, RSTn is skipped, any other marker ends the scan.</summary>
    private sealed class BitReader(byte[] data, int pos)
    {
        private int _pos = pos;
        private int _byte;
        private int _bitsLeft;
        private long _bitsRead;
        private long _mcuBits;

        public bool Ended { get; private set; }

        public int Bit()
        {
            if (_bitsLeft == 0)
            {
                if (!NextByte())
                    return -1;
            }
            _bitsLeft--;
            _bitsRead++;
            return _byte >> _bitsLeft & 1;
        }

        public bool Skip(int bits)
        {
            for (var i = 0; i < bits; i++)
            {
                if (Bit() < 0)
                    return false;
            }
            return true;
        }

        public void MarkMcu() => _mcuBits = _bitsRead;

        public long BitsSinceMcu => _bitsRead - _mcuBits;

        private bool NextByte()
        {
            while (_pos < data.Length)
            {
                var b = data[_pos++];
                if (b != 0xFF)
                {
                    _byte = b;
                    _bitsLeft = 8;
                    return true;
                }
                if (_pos >= data.Length)
                    break;
                var next = data[_pos++];
                if (next == 0x00)
                {
                    _byte = 0xFF;
                    _bitsLeft = 8;
                    return true;
                }
                if (next is >= 0xD0 and <= 0xD7)
                    continue; // RSTn; DC prediction does not matter for counting
                break; // EOI or another marker
            }
            Ended = true;
            return false;
        }
    }
}
