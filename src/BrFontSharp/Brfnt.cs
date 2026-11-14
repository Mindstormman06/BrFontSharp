using System.Buffers.Binary;
using System.Text;
using TexSharp;

namespace BrFontSharp;

public readonly record struct CharWidth(sbyte Left, byte GlyphWidth, sbyte Width);

public sealed class FontInfo
{
    public byte FontType { get; set; } = 1;
    public sbyte LineFeed { get; set; }
    public ushort AlterCharIndex { get; set; }
    public CharWidth DefaultWidth { get; set; }
    public byte Encoding { get; set; } = 1;
    public byte Height { get; set; }
    public byte Width { get; set; }
    public byte Ascent { get; set; }
    public byte Padding { get; set; }
}

public sealed class GlyphSheets
{
    public byte CellWidth { get; set; }
    public byte CellHeight { get; set; }
    public sbyte BaselinePos { get; set; }
    public byte MaxCharWidth { get; set; }
    public uint SheetSize { get; set; }
    public ushort SheetCount { get; set; }
    public ushort SheetFormat { get; set; }
    public ushort CellsPerRow { get; set; }
    public ushort CellsPerColumn { get; set; }
    public ushort SheetWidth { get; set; }
    public ushort SheetHeight { get; set; }
    public byte[] Data { get; set; } = [];

    public GxFormat Format => (GxFormat)(SheetFormat & 0x7FFF);
    public bool Compressed => (SheetFormat & 0x8000) != 0;
    public int CellsPerSheet => CellsPerRow * CellsPerColumn;

    public byte[] SheetData(int sheet)
    {
        if ((uint)sheet >= SheetCount) throw new ArgumentOutOfRangeException(nameof(sheet));
        if (!Compressed) return Data.AsSpan(sheet * (int)SheetSize, (int)SheetSize).ToArray();

        int at = 0;
        for (int i = 0; i < sheet; i++) at += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(at));
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(at));
        return Huffman.Decompress(Data.AsSpan(at + 4, length));
    }

    public byte[] SheetToRgba8(int sheet) => GxTexture.ToRgba8(Format, SheetData(sheet), SheetWidth, SheetHeight);
}

public sealed class WidthBlock
{
    public ushort First { get; set; }
    public ushort Last { get; set; }
    public List<CharWidth> Widths { get; } = [];
}

public enum MapMethod : ushort { Direct, Table, Scan }

public sealed class CodeMap
{
    public ushort Begin { get; set; }
    public ushort End { get; set; }
    public MapMethod Method { get; set; }
    public ushort Reserved { get; set; }
    public ushort DirectOffset { get; set; }
    public ushort[]? Table { get; set; }
    public List<(ushort Code, ushort Index)>? Scan { get; set; }

    public int? IndexOf(int code)
    {
        if (code < Begin || code > End) return null;
        switch (Method)
        {
            case MapMethod.Direct:
                return code - Begin + DirectOffset;
            case MapMethod.Table:
                ushort index = Table![code - Begin];
                return index == 0xFFFF ? null : index;
            default:
                foreach ((ushort c, ushort i) in Scan!) if (c == code) return i;
                return null;
        }
    }
}

public sealed record Glyph(int Index, CharWidth Width, int Sheet, int X, int Y);

public sealed class BrfntFile
{
    private const int HeaderSize = 0x10;
    private const int SheetAlignment = 0x20;

    public string Magic { get; set; } = "RFNT";
    public ushort Version { get; set; } = 0x0104;
    public FontInfo Info { get; set; } = new();
    public GlyphSheets Sheets { get; set; } = new();
    public List<WidthBlock> Widths { get; } = [];
    public List<CodeMap> Maps { get; } = [];
    public List<(string Magic, byte[] Body)> OtherSections { get; } = [];
    public List<string> SectionOrder { get; } = [];

    public static bool IsBrfnt(ReadOnlySpan<byte> data)
        => data.Length >= HeaderSize && (data[..4].SequenceEqual("RFNT"u8) || data[..4].SequenceEqual("RFNA"u8));

    public int? GlyphIndex(int code)
    {
        foreach (CodeMap map in Maps)
            if (map.IndexOf(code) is { } index) return index;
        return null;
    }

    public CharWidth WidthOf(int glyph)
    {
        foreach (WidthBlock block in Widths)
            if (glyph >= block.First && glyph <= block.Last) return block.Widths[glyph - block.First];
        return Info.DefaultWidth;
    }

    public Glyph? GetGlyph(char c) => GetGlyph((int)c);

    public Glyph? GetGlyph(int code)
    {
        int index = GlyphIndex(code) ?? (Info.AlterCharIndex is var alt && alt != 0xFFFF ? alt : -1);
        if (index < 0 || Sheets.CellsPerSheet == 0) return null;
        int sheet = index / Sheets.CellsPerSheet, cell = index % Sheets.CellsPerSheet;
        return new Glyph(index, WidthOf(index), sheet,
            cell % Sheets.CellsPerRow * (Sheets.CellWidth + 1), cell / Sheets.CellsPerRow * (Sheets.CellHeight + 1));
    }

    public IEnumerable<(int Code, int Index)> AllCodes()
        => Maps.SelectMany(m => Enumerable.Range(m.Begin, m.End - m.Begin + 1).Select(c => (c, m.IndexOf(c))))
               .Where(p => p.Item2 is not null).Select(p => (p.c, p.Item2!.Value));

    // ---- reading ----------------------------------------------------------

    public static BrfntFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public static BrfntFile FromBinary(ReadOnlySpan<byte> data)
    {
        if (!IsBrfnt(data)) throw new InvalidDataException("Not a BRFNT/BRFNA file.");
        if (BinaryPrimitives.ReadUInt16BigEndian(data[4..]) != 0xFEFF) throw new InvalidDataException("Font is not big-endian.");
        BrfntFile font = new() { Magic = Encoding.ASCII.GetString(data[..4]), Version = U16(data, 6) };

        int p = U16(data, 0x0C), count = U16(data, 0x0E);
        for (int i = 0; i < count; i++)
        {
            string magic = Encoding.ASCII.GetString(data.Slice(p, 4));
            int size = (int)U32(data, p + 4);
            if (size < 8 || p + size > data.Length) throw new InvalidDataException($"Font section {magic} at 0x{p:X} runs past the file.");
            ReadOnlySpan<byte> s = data.Slice(p, size);
            font.SectionOrder.Add(magic);
            switch (magic)
            {
                case "FINF":
                    font.Info = new FontInfo
                    {
                        FontType = s[8], LineFeed = (sbyte)s[9], AlterCharIndex = U16(s, 0x0A),
                        DefaultWidth = new CharWidth((sbyte)s[0x0C], s[0x0D], (sbyte)s[0x0E]), Encoding = s[0x0F],
                        Height = s[0x1C], Width = s[0x1D], Ascent = s[0x1E], Padding = s[0x1F],
                    };
                    break;
                case "TGLP":
                    int image = (int)U32(s, 0x1C) - p;
                    if (image < 0x20 || image > size) throw new InvalidDataException("Glyph sheet data is outside its section.");
                    font.Sheets = new GlyphSheets
                    {
                        CellWidth = s[8], CellHeight = s[9], BaselinePos = (sbyte)s[0x0A], MaxCharWidth = s[0x0B],
                        SheetSize = U32(s, 0x0C), SheetCount = U16(s, 0x10), SheetFormat = U16(s, 0x12),
                        CellsPerRow = U16(s, 0x14), CellsPerColumn = U16(s, 0x16), SheetWidth = U16(s, 0x18), SheetHeight = U16(s, 0x1A),
                        Data = s[image..].ToArray(),
                    };
                    break;
                case "CWDH":
                    WidthBlock block = new() { First = U16(s, 8), Last = U16(s, 0x0A) };
                    for (int c = 0; c <= block.Last - block.First; c++)
                        block.Widths.Add(new CharWidth((sbyte)s[0x10 + c * 3], s[0x11 + c * 3], (sbyte)s[0x12 + c * 3]));
                    font.Widths.Add(block);
                    break;
                case "CMAP":
                    CodeMap map = new() { Begin = U16(s, 8), End = U16(s, 0x0A), Method = (MapMethod)U16(s, 0x0C), Reserved = U16(s, 0x0E) };
                    switch (map.Method)
                    {
                        case MapMethod.Direct:
                            map.DirectOffset = U16(s, 0x14);
                            break;
                        case MapMethod.Table:
                            map.Table = new ushort[map.End - map.Begin + 1];
                            for (int c = 0; c < map.Table.Length; c++) map.Table[c] = U16(s, 0x14 + c * 2);
                            break;
                        default:
                            int pairs = U16(s, 0x14);
                            map.Scan = [];
                            for (int c = 0; c < pairs; c++) map.Scan.Add((U16(s, 0x16 + c * 4), U16(s, 0x18 + c * 4)));
                            break;
                    }
                    font.Maps.Add(map);
                    break;
                default:
                    font.OtherSections.Add((magic, s[8..].ToArray()));
                    break;
            }
            p += size;
        }
        return font;
    }

    // ---- writing ----------------------------------------------------------

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());

    public byte[] Write()
    {
        List<string> order = SectionOrder.Count > 0 ? [.. SectionOrder]
            : ["FINF", "TGLP", .. Widths.Select(_ => "CWDH"), .. Maps.Select(_ => "CMAP")];

        using MemoryStream o = new();
        o.Write(new byte[HeaderSize]);
        int finf = -1, tglp = -1, other = 0, widthIndex = 0, mapIndex = 0;
        List<int> widthStarts = [], mapStarts = [];

        foreach (string magic in order)
        {
            int start = (int)o.Length;
            o.Write(Encoding.ASCII.GetBytes(magic));
            o.Write(new byte[4]);
            switch (magic)
            {
                case "FINF":
                    finf = start;
                    FontInfo f = Info;
                    o.Write([f.FontType, (byte)f.LineFeed]);
                    Write16(o, f.AlterCharIndex);
                    o.Write([(byte)f.DefaultWidth.Left, f.DefaultWidth.GlyphWidth, (byte)f.DefaultWidth.Width, f.Encoding]);
                    o.Write(new byte[12]);
                    o.Write([f.Height, f.Width, f.Ascent, f.Padding]);
                    break;
                case "TGLP":
                    tglp = start;
                    GlyphSheets g = Sheets;
                    o.Write([g.CellWidth, g.CellHeight, (byte)g.BaselinePos, g.MaxCharWidth]);
                    Write32(o, g.SheetSize);
                    Write16(o, g.SheetCount);
                    Write16(o, g.SheetFormat);
                    Write16(o, g.CellsPerRow);
                    Write16(o, g.CellsPerColumn);
                    Write16(o, g.SheetWidth);
                    Write16(o, g.SheetHeight);
                    Write32(o, 0);
                    while (o.Length % SheetAlignment != 0) o.WriteByte(0);
                    Patch32(o, start + 0x1C, (uint)o.Length);
                    o.Write(g.Data);
                    break;
                case "CWDH":
                    widthStarts.Add(start);
                    WidthBlock b = Widths[widthIndex++];
                    Write16(o, b.First);
                    Write16(o, b.Last);
                    Write32(o, 0);
                    foreach (CharWidth w in b.Widths) o.Write([(byte)w.Left, w.GlyphWidth, (byte)w.Width]);
                    while (o.Length % 4 != 0) o.WriteByte(0);
                    break;
                case "CMAP":
                    mapStarts.Add(start);
                    CodeMap m = Maps[mapIndex++];
                    Write16(o, m.Begin);
                    Write16(o, m.End);
                    Write16(o, (ushort)m.Method);
                    Write16(o, m.Reserved);
                    Write32(o, 0);
                    switch (m.Method)
                    {
                        case MapMethod.Direct: Write16(o, m.DirectOffset); break;
                        case MapMethod.Table: foreach (ushort i in m.Table!) Write16(o, i); break;
                        default:
                            Write16(o, (ushort)m.Scan!.Count);
                            foreach ((ushort code, ushort index) in m.Scan) { Write16(o, code); Write16(o, index); }
                            break;
                    }
                    while (o.Length % 4 != 0) o.WriteByte(0);
                    break;
                default:
                    o.Write(OtherSections[other++].Body);
                    break;
            }
            Patch32(o, start + 4, (uint)(o.Length - start));
        }

        for (int i = 0; i < widthStarts.Count; i++)
            Patch32(o, widthStarts[i] + 0x0C, i + 1 < widthStarts.Count ? (uint)(widthStarts[i + 1] + 8) : 0);
        for (int i = 0; i < mapStarts.Count; i++)
            Patch32(o, mapStarts[i] + 0x10, i + 1 < mapStarts.Count ? (uint)(mapStarts[i + 1] + 8) : 0);
        if (finf >= 0)
        {
            Patch32(o, finf + 0x10, tglp >= 0 ? (uint)(tglp + 8) : 0);
            Patch32(o, finf + 0x14, widthStarts.Count > 0 ? (uint)(widthStarts[0] + 8) : 0);
            Patch32(o, finf + 0x18, mapStarts.Count > 0 ? (uint)(mapStarts[0] + 8) : 0);
        }

        byte[] file = o.ToArray();
        Encoding.ASCII.GetBytes(Magic, file);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), Version);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)file.Length);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x0C), HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(0x0E), (ushort)order.Count);
        return file;
    }

    private static ushort U16(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d[at..]);

    private static void Write16(Stream o, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); o.Write(b); }
    private static void Write32(Stream o, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.Write(b); }

    private static void Patch32(MemoryStream o, int at, uint v)
    {
        long position = o.Position;
        o.Position = at;
        Write32(o, v);
        o.Position = position;
    }
}
