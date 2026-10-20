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
