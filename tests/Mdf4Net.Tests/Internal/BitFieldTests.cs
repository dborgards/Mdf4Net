using System;
using AwesomeAssertions;
using Mdf4Net.Internal;
using Xunit;

namespace Mdf4Net.Tests.Internal;

public class BitFieldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_Offset_And_Width_Round_Trips_Without_Touching_Neighbouring_Bits(bool bigEndian)
    {
        var random = new Random(1234);
        for (int bitCount = 1; bitCount <= 64; bitCount++)
        {
            for (int bitOffset = 0; bitOffset < 8; bitOffset++)
            {
                var record = new byte[16];
                random.NextBytes(record);
                byte[] before = (byte[])record.Clone();
                ulong value = (((ulong)random.Next() << 33) ^ ((ulong)random.Next() << 2) ^ (ulong)random.Next()) & BitField.Mask(bitCount);

                BitField.WriteUnsigned(record, 3, bitOffset, bitCount, bigEndian, value);

                BitField.ReadUnsigned(record, 3, bitOffset, bitCount, bigEndian).Should().Be(value, $"{bitCount} bits at bit {bitOffset}");

                // Writing the original bits back must restore the record exactly.
                ulong original = BitField.ReadUnsigned(before, 3, bitOffset, bitCount, bigEndian);
                BitField.WriteUnsigned(record, 3, bitOffset, bitCount, bigEndian, original);
                record.Should().Equal(before, $"{bitCount} bits at bit {bitOffset} must not disturb other bits");
            }
        }
    }

    [Fact]
    public void Little_Endian_Layout_Matches_The_Mdf_Convention()
    {
        var record = new byte[4];
        BitField.WriteUnsigned(record, 0, 4, 12, bigEndian: false, 0xABC);

        // Value 0xABC shifted by 4: 0xABC0, little endian.
        record.Should().Equal(0xC0, 0xAB, 0x00, 0x00);
    }

    [Fact]
    public void Big_Endian_Layout_Matches_The_Mdf_Convention()
    {
        var record = new byte[4];
        BitField.WriteUnsigned(record, 0, 0, 16, bigEndian: true, 0x1234);

        record.Should().Equal(0x12, 0x34, 0x00, 0x00);
    }

    [Theory]
    [InlineData(12, -2048)]
    [InlineData(12, 2047)]
    [InlineData(12, -1)]
    [InlineData(64, long.MinValue)]
    [InlineData(64, long.MaxValue)]
    [InlineData(1, -1)]
    public void Signed_Values_Are_Sign_Extended(int bitCount, long value)
    {
        var record = new byte[10];
        BitField.WriteUnsigned(record, 0, 3, bitCount, bigEndian: false, unchecked((ulong)value));

        BitField.ReadSigned(record, 0, 3, bitCount, bigEndian: false).Should().Be(value);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-2.5)]
    [InlineData(65504.0)]
    [InlineData(0.000061035156)]
    [InlineData(5.9604645E-08)]
    public void Half_Precision_Round_Trips_Representable_Values(double value)
    {
        ushort bits = BitField.DoubleToHalfBits(value);

        BitField.HalfBitsToDouble(bits).Should().BeApproximately(value, Math.Abs(value) * 1e-6 + 1e-12);
    }

    [Fact]
    public void Half_Precision_Saturates_To_Infinity()
    {
        BitField.HalfBitsToDouble(BitField.DoubleToHalfBits(1e6)).Should().Be(double.PositiveInfinity);
        double.IsNaN(BitField.HalfBitsToDouble(BitField.DoubleToHalfBits(double.NaN))).Should().BeTrue();
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void Floats_Round_Trip(int bitCount, bool bigEndian)
    {
        var record = new byte[12];
        BitField.WriteFloat(record, 2, bitCount, bigEndian, -1234.5);

        BitField.ReadFloat(record, 2, bitCount, bigEndian).Should().Be(-1234.5);
    }
}
