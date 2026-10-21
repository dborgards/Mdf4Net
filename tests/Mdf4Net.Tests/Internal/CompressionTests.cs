using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Mdf4Net.Internal;
using Xunit;

namespace Mdf4Net.Tests.Internal;

public class CompressionTests
{
    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    public void The_Inflater_Reads_What_Deflate_Writes(CompressionLevel level)
    {
        var random = new Random(7);
        foreach (int length in new[] { 0, 1, 100, 65_535, 65_536, 300_000 })
        {
            foreach (int pattern in new[] { 0, 1, 2 })
            {
                var data = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    data[i] = pattern switch { 0 => (byte)random.Next(256), 1 => (byte)(i % 7), _ => (byte)(i / 1000) };
                }

                byte[] compressed = Zlib.Compress(data, 0, data.Length, level);
                Zlib.Decompress(compressed, data.Length, 0).Should().Equal(data);
            }
        }
    }

    [Fact]
    public void Bytes_Between_The_Deflate_Data_And_The_Trailer_Are_Refused()
    {
        byte[] data = Encoding.ASCII.GetBytes("a few bytes, a few bytes, a few bytes");
        byte[] compressed = Zlib.Compress(data, 0, data.Length);
        var padded = new byte[compressed.Length + 1];
        Array.Copy(compressed, padded, compressed.Length - 4);
        Array.Copy(compressed, compressed.Length - 4, padded, padded.Length - 4, 4);

        Action read = () => Zlib.Decompress(padded, data.Length, 0);

        read.Should().Throw<Mdf4FormatException>().WithMessage("*between its deflate data*");
    }

    [Fact]
    public void Damaged_Streams_Fail_Only_With_A_Format_Exception()
    {
        var random = new Random(11);
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(random.Next(4) * 40);
        }

        byte[] compressed = Zlib.Compress(data, 0, data.Length);
        for (int round = 0; round < 3000; round++)
        {
            byte[] damaged = (byte[])compressed.Clone();
            for (int hits = random.Next(1, 4); hits > 0; hits--)
            {
                damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            }

            byte[] cut = round % 5 == 0 ? damaged.AsSpan(0, random.Next(damaged.Length)).ToArray() : damaged;
            Exception? failure = Record.Exception(() => Zlib.Decompress(cut, data.Length, 0));
            (failure is null or Mdf4FormatException).Should().BeTrue($"round {round} failed with {failure}");
        }
    }

    [Fact]
    public void Adler32_Matches_The_Reference_Value()
    {
        // The example from RFC 1950 / Wikipedia: Adler-32 of "Wikipedia" is 0x11E60398.
        byte[] data = Encoding.ASCII.GetBytes("Wikipedia");

        Zlib.Adler32(data, 0, data.Length).Should().Be(0x11E60398u);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5551)]
    [InlineData(5553)]
    [InlineData(200_000)]
    public void Zlib_Round_Trips(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        for (int i = 0; i < length; i += 3)
        {
            data[i] = 0; // some redundancy, so deflate actually compresses
        }

        byte[] compressed = Zlib.Compress(data, 0, data.Length);

        Zlib.Decompress(compressed, data.Length, 0).Should().Equal(data);
    }

    [Fact]
    public void Zlib_Output_Is_A_Standard_Zlib_Stream()
    {
        byte[] data = Encoding.UTF8.GetBytes(new string('m', 1000) + "df");
        byte[] compressed = Zlib.Compress(data, 0, data.Length);

        // Strip the 2-byte header and the 4-byte trailer: what remains is plain deflate.
        using var raw = new MemoryStream(compressed, 2, compressed.Length - 6);
        using var inflate = new DeflateStream(raw, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);

        output.ToArray().Should().Equal(data);
    }

    [Fact]
    public void Corrupt_Checksum_Is_Rejected()
    {
        byte[] data = Encoding.UTF8.GetBytes("measurement data format");
        byte[] compressed = Zlib.Compress(data, 0, data.Length);
        compressed[compressed.Length - 1] ^= 0xFF;

        Action act = () => Zlib.Decompress(compressed, data.Length, 0x40);

        act.Should().Throw<Mdf4FormatException>().WithMessage("*Adler-32*");
    }

    [Fact]
    public void A_Stream_Longer_Than_Declared_Is_Rejected()
    {
        byte[] data = new byte[1000];
        byte[] compressed = Zlib.Compress(data, 0, data.Length);

        Action act = () => Zlib.Decompress(compressed, 10, 0x40);

        act.Should().Throw<Mdf4FormatException>().WithMessage("*more than*");
    }

    [Fact]
    public void A_Stream_Shorter_Than_Declared_Is_Rejected()
    {
        byte[] data = new byte[1000];
        byte[] compressed = Zlib.Compress(data, 0, data.Length);

        Action act = () => Zlib.Decompress(compressed, 5000, 0x40);

        act.Should().Throw<Mdf4FormatException>().WithMessage("*declares 5000*");
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(10, 3)]
    [InlineData(24, 8)]
    [InlineData(25, 8)]
    [InlineData(7, 8)]
    [InlineData(1000, 17)]
    public void Transposition_Round_Trips_Including_The_Remainder(int length, int columns)
    {
        var data = new byte[length];
        new Random(length * columns).NextBytes(data);

        byte[] transposed = Transposition.Transpose(data, 0, length, columns);

        Transposition.Untranspose(transposed, columns).Should().Equal(data);
    }

    [Fact]
    public void Transposition_Writes_Columns_First()
    {
        byte[] data = { 1, 2, 3, 4, 5, 6, 9 };

        // Two rows of three columns, plus one untransposed trailing byte.
        Transposition.Transpose(data, 0, data.Length, 3).Should().Equal(1, 4, 2, 5, 3, 6, 9);
    }
}
