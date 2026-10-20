using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Mdf4Net.Internal;

/// <summary>
/// Reads and writes integer and floating-point values at an arbitrary bit position of a record.
/// </summary>
/// <remarks>
/// Little endian: the value's bytes start at <c>byteOffset</c>, are read as a little-endian
/// integer, shifted right by <c>bitOffset</c> and masked to <c>bitCount</c> bits.
/// Big endian: the same bytes are read as a big-endian integer and then shifted and masked the
/// same way, which is how MDF 4 lays out Motorola signals that start at a byte boundary.
/// </remarks>
internal static class BitField
{
    public static int ByteSpan(int bitOffset, int bitCount) => (bitOffset + bitCount + 7) >> 3;

    public static ulong Mask(int bitCount) => bitCount >= 64 ? ulong.MaxValue : (1UL << bitCount) - 1;

    public static ulong ReadUnsigned(ReadOnlySpan<byte> record, int byteOffset, int bitOffset, int bitCount, bool bigEndian)
    {
        int span = ByteSpan(bitOffset, bitCount);
        ReadOnlySpan<byte> bytes = record.Slice(byteOffset, span);
        ulong value;
        if (span <= 8)
        {
            value = 0;
            if (bigEndian)
            {
                for (int i = 0; i < span; i++)
                {
                    value = (value << 8) | bytes[i];
                }
            }
            else
            {
                for (int i = span - 1; i >= 0; i--)
                {
                    value = (value << 8) | bytes[i];
                }
            }

            value >>= bitOffset;
        }
        else
        {
            // Nine bytes: a 57..64-bit value that does not start at a byte boundary.
            ulong low;
            byte extra;
            if (bigEndian)
            {
                low = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(1));
                extra = bytes[0];
            }
            else
            {
                low = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                extra = bytes[8];
            }

            value = (low >> bitOffset) | ((ulong)extra << (64 - bitOffset));
        }

        return value & Mask(bitCount);
    }

    public static long ReadSigned(ReadOnlySpan<byte> record, int byteOffset, int bitOffset, int bitCount, bool bigEndian)
    {
        ulong raw = ReadUnsigned(record, byteOffset, bitOffset, bitCount, bigEndian);
        if (bitCount < 64 && (raw & (1UL << (bitCount - 1))) != 0)
        {
            raw |= ~Mask(bitCount);
        }

        return unchecked((long)raw);
    }

    public static void WriteUnsigned(Span<byte> record, int byteOffset, int bitOffset, int bitCount, bool bigEndian, ulong value)
    {
        value &= Mask(bitCount);
        int span = ByteSpan(bitOffset, bitCount);
        Span<byte> bytes = record.Slice(byteOffset, span);
        if (span > 8)
        {
            // Nine-byte case: split into the low eight bytes and one extra.
            ulong mask = Mask(bitCount) << bitOffset;
            ulong lowPart = value << bitOffset;
            byte extraMask = (byte)(Mask(bitCount) >> (64 - bitOffset));
            byte extraPart = (byte)(value >> (64 - bitOffset));
            if (bigEndian)
            {
                ulong old = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(1));
                BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(1), (old & ~mask) | lowPart);
                bytes[0] = (byte)((bytes[0] & ~extraMask) | extraPart);
            }
            else
            {
                ulong old = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, (old & ~mask) | lowPart);
                bytes[8] = (byte)((bytes[8] & ~extraMask) | extraPart);
            }

            return;
        }

        ulong current = 0;
        if (bigEndian)
        {
            for (int i = 0; i < span; i++)
            {
                current = (current << 8) | bytes[i];
            }
        }
        else
        {
            for (int i = span - 1; i >= 0; i--)
            {
                current = (current << 8) | bytes[i];
            }
        }

        ulong fieldMask = Mask(bitCount) << bitOffset;
        current = (current & ~fieldMask) | (value << bitOffset);

        if (bigEndian)
        {
            for (int i = span - 1; i >= 0; i--)
            {
                bytes[i] = (byte)current;
                current >>= 8;
            }
        }
        else
        {
            for (int i = 0; i < span; i++)
            {
                bytes[i] = (byte)current;
                current >>= 8;
            }
        }
    }

    public static double ReadFloat(ReadOnlySpan<byte> record, int byteOffset, int bitCount, bool bigEndian)
    {
        switch (bitCount)
        {
            case 64:
                {
                    ulong bits = bigEndian
                        ? BinaryPrimitives.ReadUInt64BigEndian(record.Slice(byteOffset, 8))
                        : BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(byteOffset, 8));
                    return BitConverter.Int64BitsToDouble(unchecked((long)bits));
                }

            case 32:
                {
                    uint bits = bigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(record.Slice(byteOffset, 4))
                        : BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(byteOffset, 4));
                    return UInt32BitsToSingle(bits);
                }

            case 16:
                {
                    ushort bits = bigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(record.Slice(byteOffset, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(byteOffset, 2));
                    return HalfBitsToDouble(bits);
                }

            default:
                throw new Mdf4FormatException($"A floating-point channel of {bitCount} bits is not defined by MDF 4.");
        }
    }

    public static void WriteFloat(Span<byte> record, int byteOffset, int bitCount, bool bigEndian, double value)
    {
        switch (bitCount)
        {
            case 64:
                {
                    ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
                    if (bigEndian)
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(record.Slice(byteOffset, 8), bits);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(byteOffset, 8), bits);
                    }

                    return;
                }

            case 32:
                {
                    uint bits = SingleToUInt32Bits((float)value);
                    if (bigEndian)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(record.Slice(byteOffset, 4), bits);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(byteOffset, 4), bits);
                    }

                    return;
                }

            case 16:
                {
                    ushort bits = DoubleToHalfBits(value);
                    if (bigEndian)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(record.Slice(byteOffset, 2), bits);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(byteOffset, 2), bits);
                    }

                    return;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "A floating-point channel has 16, 32 or 64 bits.");
        }
    }

    private static float UInt32BitsToSingle(uint bits)
    {
        Span<uint> source = stackalloc uint[1];
        source[0] = bits;
        return MemoryMarshal.Cast<uint, float>(source)[0];
    }

    private static uint SingleToUInt32Bits(float value)
    {
        Span<float> source = stackalloc float[1];
        source[0] = value;
        return MemoryMarshal.Cast<float, uint>(source)[0];
    }

    internal static double HalfBitsToDouble(ushort bits)
    {
        int sign = (bits >> 15) & 1;
        int exponent = (bits >> 10) & 0x1F;
        int mantissa = bits & 0x3FF;
        double value;
        if (exponent == 0)
        {
            value = mantissa * Math.Pow(2, -24);
        }
        else if (exponent == 0x1F)
        {
            value = mantissa == 0 ? double.PositiveInfinity : double.NaN;
        }
        else
        {
            value = (1 + (mantissa / 1024.0)) * Math.Pow(2, exponent - 15);
        }

        return sign == 1 ? -value : value;
    }

    internal static ushort DoubleToHalfBits(double value)
    {
        if (double.IsNaN(value))
        {
            return 0x7E00;
        }

        ushort sign = (ushort)(value < 0 || (value == 0 && 1 / value < 0) ? 0x8000 : 0);
        double magnitude = Math.Abs(value);
        if (magnitude >= 65520)
        {
            return (ushort)(sign | 0x7C00);
        }

        if (magnitude < Math.Pow(2, -24) / 2)
        {
            return sign;
        }

        int exponent = (int)Math.Floor(Math.Log(magnitude, 2));
        if (exponent < -14)
        {
            int subnormal = (int)Math.Round(magnitude / Math.Pow(2, -24), MidpointRounding.ToEven);
            return (ushort)(sign | subnormal);
        }

        double scaled = (magnitude / Math.Pow(2, exponent)) - 1;
        int mantissa = (int)Math.Round(scaled * 1024, MidpointRounding.ToEven);
        if (mantissa == 1024)
        {
            mantissa = 0;
            exponent++;
        }

        if (exponent > 15)
        {
            return (ushort)(sign | 0x7C00);
        }

        return (ushort)(sign | ((exponent + 15) << 10) | mantissa);
    }
}
