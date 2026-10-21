using System;

namespace Mdf4Net.Internal;

/// <summary>
/// A raw deflate (RFC 1951) decoder for untrusted input. Unlike <c>DeflateStream</c> it knows
/// exactly where the deflate data ends, so bytes between it and the zlib trailer are detected,
/// and its output can never exceed the size the caller expects.
/// </summary>
internal sealed class Inflater
{
    private const int MaxBits = 15;
    private const int MaxLengthCodes = 286;
    private const int MaxDistanceCodes = 30;
    private const int FixedLengthCodes = 288;

    private static readonly short[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly short[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    private static readonly short[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly short[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
    private static readonly byte[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private static readonly Huffman FixedLengths;
    private static readonly Huffman FixedDistances;

    private readonly byte[] _input;
    private readonly int _inputEnd;
    private readonly byte[] _output;
    private readonly long _blockOffset;
    private int _position;
    private int _bitBuffer;
    private int _bitCount;
    private int _written;

    static Inflater()
    {
        var lengths = new short[FixedLengthCodes];
        for (int i = 0; i < FixedLengthCodes; i++)
        {
            lengths[i] = (short)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        }

        FixedLengths = new Huffman(lengths, FixedLengthCodes, out _);
        var distances = new short[MaxDistanceCodes];
        for (int i = 0; i < distances.Length; i++)
        {
            distances[i] = 5;
        }

        FixedDistances = new Huffman(distances, MaxDistanceCodes, out _);
    }

    private Inflater(byte[] input, int start, int end, byte[] output, long blockOffset)
    {
        _input = input;
        _position = start;
        _inputEnd = end;
        _output = output;
        _blockOffset = blockOffset;
    }

    /// <summary>
    /// Inflates the deflate data in <c>input[start..end)</c> into <paramref name="output"/>, which
    /// has to be filled exactly, and returns the end of the deflate data (the first byte after it).
    /// </summary>
    public static int Inflate(byte[] input, int start, int end, byte[] output, long blockOffset)
    {
        var inflater = new Inflater(input, start, end, output, blockOffset);
        inflater.Run();
        return inflater._position;
    }

    private void Run()
    {
        bool last;
        do
        {
            last = Bits(1) == 1;
            switch (Bits(2))
            {
                case 0:
                    Stored();
                    break;
                case 1:
                    Codes(FixedLengths, FixedDistances);
                    break;
                case 2:
                    Dynamic();
                    break;
                default:
                    throw Corrupt("uses the reserved block type");
            }
        }
        while (!last);

        if (_written != _output.Length)
        {
            throw Mdf4FormatException.At(_blockOffset, $"DZ block inflates to {_written} bytes, the block declares {_output.Length}");
        }
    }

    private Mdf4FormatException Corrupt(string what) => Mdf4FormatException.At(_blockOffset, $"DZ block holds corrupt deflate data: it {what}");

    private int Bits(int need)
    {
        while (_bitCount < need)
        {
            if (_position >= _inputEnd)
            {
                throw Corrupt("ends before the last block does");
            }

            _bitBuffer |= _input[_position++] << _bitCount;
            _bitCount += 8;
        }

        int value = _bitBuffer & ((1 << need) - 1);
        _bitBuffer >>= need;
        _bitCount -= need;
        return value;
    }

    private void Stored()
    {
        // The rest of the current byte is padding. Whole bytes are never buffered ahead (Bits reads
        // a byte only when it needs one), so the next byte of input is the next byte of the block.
        _bitBuffer = 0;
        _bitCount = 0;
        if (_inputEnd - _position < 4)
        {
            throw Corrupt("ends inside a stored block header");
        }

        int length = _input[_position] | (_input[_position + 1] << 8);
        int complement = _input[_position + 2] | (_input[_position + 3] << 8);
        _position += 4;
        if (length != (~complement & 0xFFFF))
        {
            throw Corrupt("has a stored block whose length and its complement differ");
        }

        if (_inputEnd - _position < length)
        {
            throw Corrupt("ends inside a stored block");
        }

        RequireRoom(length);
        Buffer.BlockCopy(_input, _position, _output, _written, length);
        _position += length;
        _written += length;
    }

    private void RequireRoom(int count)
    {
        if (count > _output.Length - _written)
        {
            throw Mdf4FormatException.At(_blockOffset, $"DZ block inflates to more than the {_output.Length} bytes it declares");
        }
    }

    private void Dynamic()
    {
        int lengthCodes = Bits(5) + 257;
        int distanceCodes = Bits(5) + 1;
        int codeLengthCodes = Bits(4) + 4;
        if (lengthCodes > MaxLengthCodes || distanceCodes > MaxDistanceCodes)
        {
            throw Corrupt("declares too many codes");
        }

        var lengths = new short[MaxLengthCodes + MaxDistanceCodes];
        for (int i = 0; i < codeLengthCodes; i++)
        {
            lengths[CodeLengthOrder[i]] = (short)Bits(3);
        }

        var codeLengths = new Huffman(lengths, 19, out int left);
        if (left != 0)
        {
            throw Corrupt("has an incomplete code length code");
        }

        Array.Clear(lengths, 0, lengths.Length);
        int index = 0;
        while (index < lengthCodes + distanceCodes)
        {
            int symbol = codeLengths.Decode(this);
            if (symbol < 16)
            {
                lengths[index++] = (short)symbol;
                continue;
            }

            short previous = 0;
            int repeat;
            if (symbol == 16)
            {
                if (index == 0)
                {
                    throw Corrupt("repeats a code length before there is one");
                }

                previous = lengths[index - 1];
                repeat = 3 + Bits(2);
            }
            else if (symbol == 17)
            {
                repeat = 3 + Bits(3);
            }
            else
            {
                repeat = 11 + Bits(7);
            }

            if (index + repeat > lengthCodes + distanceCodes)
            {
                throw Corrupt("repeats more code lengths than it declares");
            }

            while (repeat-- > 0)
            {
                lengths[index++] = previous;
            }
        }

        if (lengths[256] == 0)
        {
            throw Corrupt("has no end-of-block code");
        }

        var lengthTable = new Huffman(lengths, lengthCodes, out left);
        if (left < 0 || (left > 0 && lengthCodes - lengthTable.CountOf(0) != 1))
        {
            throw Corrupt("has an over-subscribed or incomplete length code");
        }

        var distanceLengths = new short[distanceCodes];
        Array.Copy(lengths, lengthCodes, distanceLengths, 0, distanceCodes);
        var distanceTable = new Huffman(distanceLengths, distanceCodes, out left);
        if (left < 0 || (left > 0 && distanceCodes - distanceTable.CountOf(0) != 1))
        {
            throw Corrupt("has an over-subscribed or incomplete distance code");
        }

        Codes(lengthTable, distanceTable);
    }

    private void Codes(Huffman lengthTable, Huffman distanceTable)
    {
        while (true)
        {
            int symbol = lengthTable.Decode(this);
            if (symbol < 256)
            {
                RequireRoom(1);
                _output[_written++] = (byte)symbol;
                continue;
            }

            if (symbol == 256)
            {
                return;
            }

            symbol -= 257;
            if (symbol >= LengthBase.Length)
            {
                throw Corrupt("uses an invalid length code");
            }

            int length = LengthBase[symbol] + Bits(LengthExtra[symbol]);
            int distanceSymbol = distanceTable.Decode(this);
            if (distanceSymbol >= MaxDistanceCodes)
            {
                throw Corrupt("uses an invalid distance code");
            }

            int distance = DistanceBase[distanceSymbol] + Bits(DistanceExtra[distanceSymbol]);
            if (distance > _written)
            {
                throw Corrupt("refers to data before its start");
            }

            RequireRoom(length);
            for (int i = 0; i < length; i++)
            {
                _output[_written] = _output[_written - distance];
                _written++;
            }
        }
    }

    private sealed class Huffman
    {
        private readonly short[] _count = new short[MaxBits + 1];
        private readonly short[] _symbol;

        /// <summary>Builds the decoding table; <paramref name="left"/> is negative if over-subscribed, positive if incomplete.</summary>
        public Huffman(short[] lengths, int count, out int left)
        {
            _symbol = new short[count];
            for (int i = 0; i < count; i++)
            {
                _count[lengths[i]]++;
            }

            left = _count[0] == count ? 0 : CodeSpaceLeft();
            if (left >= 0 && _count[0] != count)
            {
                AssignSymbols(lengths, count);
            }
        }

        private int CodeSpaceLeft()
        {
            int left = 1;
            for (int length = 1; length <= MaxBits; length++)
            {
                left = (left << 1) - _count[length];
                if (left < 0)
                {
                    break;
                }
            }

            return left;
        }

        private void AssignSymbols(short[] lengths, int count)
        {
            var offsets = new short[MaxBits + 1];
            for (int length = 1; length < MaxBits; length++)
            {
                offsets[length + 1] = (short)(offsets[length] + _count[length]);
            }

            for (int symbol = 0; symbol < count; symbol++)
            {
                if (lengths[symbol] != 0)
                {
                    _symbol[offsets[lengths[symbol]]++] = (short)symbol;
                }
            }
        }

        public int CountOf(int length) => _count[length];

        public int Decode(Inflater inflater)
        {
            int code = 0, first = 0, index = 0;
            for (int length = 1; length <= MaxBits; length++)
            {
                code |= inflater.Bits(1);
                int count = _count[length];
                if (code - count < first)
                {
                    return _symbol[index + (code - first)];
                }

                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }

            throw inflater.Corrupt("contains a bit sequence that is no code");
        }
    }
}
