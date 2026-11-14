using System.Buffers.Binary;

namespace BrFontSharp;

/// <summary>Nintendo CX Huffman decompression (types 0x24 and 0x28), as used by archived font sheets.</summary>
public static class Huffman
{
    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        if (src.Length < 5 || (src[0] & 0xF0) != 0x20) throw new InvalidDataException("Not Huffman-compressed data.");
        int bits = src[0] & 0x0F;
        if (bits is not (4 or 8)) throw new InvalidDataException($"Huffman data unit of {bits} bits is not supported.");
        int size = src[1] | src[2] << 8 | src[3] << 16;
        byte[] output = new byte[size];

        const int tree = 4;
        int treeSize = (src[tree] + 1) * 2;
        int stream = tree + treeSize;
        int outBits = 0, outByte = 0, o = 0;
        int node = tree + 1;

        for (int p = stream; o < size; p += 4)
        {
            if (p + 4 > src.Length) throw new InvalidDataException("Huffman stream ends early.");
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]);
            for (int b = 31; b >= 0 && o < size; b--)
            {
                int bit = (int)(word >> b & 1);
                byte n = src[node];
                int child = (node & ~1) + (n & 0x3F) * 2 + 2 + bit;
                bool leaf = (n & (bit == 0 ? 0x80 : 0x40)) != 0;
                if (!leaf)
                {
                    node = child;
                    continue;
                }

                int value = src[child];
                node = tree + 1;
                if (bits == 8)
                {
                    output[o++] = (byte)value;
                }
                else
                {
                    outByte |= (value & 0xF) << outBits;
                    outBits += 4;
                    if (outBits == 8) { output[o++] = (byte)outByte; outByte = 0; outBits = 0; }
                }
            }
        }
        return output;
    }
}
