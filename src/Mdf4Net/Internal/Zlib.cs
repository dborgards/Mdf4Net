using System;
using System.IO;
using System.IO.Compression;

namespace Mdf4Net.Internal;

/// <summary>
/// zlib (RFC 1950) framing around raw deflate, which is what DZ blocks store. Implemented here
/// rather than with <c>ZLibStream</c> so that both target frameworks share one code path.
/// </summary>
internal static class Zlib
{
    public static byte[] Compress(byte[] data, int offset, int count, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream(Math.Max(64, count / 2));

        // CMF 0x78: deflate, 32 KiB window. FLG 0x9C: default level, no dictionary, check bits.
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(data, offset, count);
        }

        uint adler = Adler32(data, offset, count);
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
        return output.ToArray();
    }

    /// <summary>
    /// Inflates exactly <paramref name="expectedLength"/> bytes and verifies the Adler-32 trailer.
    /// Never allocates more than <paramref name="expectedLength"/>, whatever the input says.
    /// </summary>
    public static byte[] Decompress(byte[] compressed, int expectedLength, long blockOffset)
    {
        if (compressed.Length < 6)
        {
            throw Mdf4FormatException.At(blockOffset, "DZ block data is too short for a zlib stream");
        }

        int cmf = compressed[0];
        int flg = compressed[1];
        if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
        {
            throw Mdf4FormatException.At(blockOffset, "DZ block data is not a zlib stream");
        }

        var result = new byte[expectedLength];
        try
        {
            using var input = new MemoryStream(compressed, 2, compressed.Length - 6, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            int total = 0;
            while (total < expectedLength)
            {
                int read = inflate.Read(result, total, expectedLength - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != expectedLength)
            {
                throw Mdf4FormatException.At(blockOffset, $"DZ block inflates to {total} bytes, the block declares {expectedLength}");
            }

            if (inflate.ReadByte() != -1)
            {
                throw Mdf4FormatException.At(blockOffset, $"DZ block inflates to more than the {expectedLength} bytes it declares");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
        {
            throw new Mdf4FormatException($"DZ block at 0x{blockOffset:X} holds corrupt deflate data.", ex);
        }

        int t = compressed.Length - 4;
        uint expected = ((uint)compressed[t] << 24) | ((uint)compressed[t + 1] << 16) | ((uint)compressed[t + 2] << 8) | compressed[t + 3];
        if (Adler32(result, 0, result.Length) != expected)
        {
            throw Mdf4FormatException.At(blockOffset, "DZ block fails its Adler-32 check");
        }

        return result;
    }

    public static uint Adler32(byte[] data, int offset, int count)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        int end = offset + count;
        int i = offset;
        while (i < end)
        {
            // 5552 is the largest n for which the sums cannot overflow 32 bits before the modulo.
            int chunkEnd = Math.Min(end, i + 5552);
            for (; i < chunkEnd; i++)
            {
                a += data[i];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        return (b << 16) | a;
    }
}
