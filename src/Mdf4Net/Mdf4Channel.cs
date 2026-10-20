using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>A channel (CN block): one signal of a channel group.</summary>
public sealed class Mdf4Channel
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    private readonly List<Mdf4Channel> _children = new();
    private readonly long _dataLink;
    private Mdf4ChannelGroup? _signalDataGroup;
    private Mdf4Channel? _lengthChannel;
    private byte[]? _signalData;

    internal Mdf4Channel(Mdf4ChannelGroup group, Mdf4Channel? parent, RawBlock block, HashSet<long> visited, int depth)
    {
        BlockReader reader = group.DataGroup.File.Reader;
        ChannelGroup = group;
        Parent = parent;
        Offset = block.Offset;

        ChannelType = (Mdf4ChannelType)block.U8(0);
        SyncType = (Mdf4SyncType)block.U8(1);
        DataType = (Mdf4DataType)block.U8(2);
        BitOffset = block.U8(3);
        ByteOffset = block.U32(4);
        BitCount = block.U32(8);
        Flags = (Mdf4ChannelFlags)block.U32(12);
        InvalidationBitPosition = block.U32(16);
        Precision = (Flags & Mdf4ChannelFlags.PrecisionValid) != 0 ? block.U8(20) : null;
        if ((Flags & Mdf4ChannelFlags.ValueRangeValid) != 0 && block.HasData(24, 16))
        {
            MinimumValue = block.F64(24);
            MaximumValue = block.F64(32);
        }

        _dataLink = block.Link(5);
        Name = TextBlocks.ReadText(reader, block.Link(2)) ?? string.Empty;
        Source = Mdf4SourceInformation.Read(reader, block.Link(3));
        Conversion = Mdf4Conversion.Read(reader, block.Link(4), 0);
        Unit = TextBlocks.ReadText(reader, block.Link(6)) ?? Conversion?.Unit;
        Comment = TextBlocks.ReadText(reader, block.Link(7));

        long composition = block.Link(1);
        if (composition != 0)
        {
            if (depth >= reader.Options.MaxNestingDepth)
            {
                throw Mdf4FormatException.At(block.Offset, "Channel compositions are nested deeper than MaxNestingDepth");
            }

            BlockHeader header = reader.ReadHeader(composition);
            if (header.Id == "##CA")
            {
                IsArray = true;
            }
            else
            {
                for (long cn = composition; cn != 0;)
                {
                    BlockReader.Visit(visited, cn, "CN");
                    RawBlock child = reader.ReadBlock(cn, "##CN");
                    _children.Add(new Mdf4Channel(group, this, child, visited, depth + 1));
                    cn = child.Link(0);
                }
            }
        }
    }

    /// <summary>The channel group this channel belongs to.</summary>
    public Mdf4ChannelGroup ChannelGroup { get; }

    /// <summary>The enclosing structure channel, for a composition member.</summary>
    public Mdf4Channel? Parent { get; }

    /// <summary>File offset of the CN block.</summary>
    public long Offset { get; }

    /// <summary>The channel name.</summary>
    public string Name { get; }

    /// <summary>The channel comment.</summary>
    public string? Comment { get; }

    /// <summary>The physical unit: the channel's own, or else the conversion's.</summary>
    public string? Unit { get; }

    /// <summary>The kind of channel.</summary>
    public Mdf4ChannelType ChannelType { get; }

    /// <summary>What a master channel measures.</summary>
    public Mdf4SyncType SyncType { get; }

    /// <summary>The raw value encoding.</summary>
    public Mdf4DataType DataType { get; }

    /// <summary>Byte offset of the value in the record (after the record id).</summary>
    public uint ByteOffset { get; }

    /// <summary>Bit offset of the value within its first byte (0..7).</summary>
    public byte BitOffset { get; }

    /// <summary>Number of bits of the raw value.</summary>
    public uint BitCount { get; }

    /// <summary>The channel flags.</summary>
    public Mdf4ChannelFlags Flags { get; }

    /// <summary>Position of the invalidation bit, valid if <see cref="Mdf4ChannelFlags.InvalidationBitValid"/> is set.</summary>
    public uint InvalidationBitPosition { get; }

    /// <summary>Decimal places for display, if declared.</summary>
    public byte? Precision { get; }

    /// <summary>The lower end of the value range, if declared.</summary>
    public double? MinimumValue { get; }

    /// <summary>The upper end of the value range, if declared.</summary>
    public double? MaximumValue { get; }

    /// <summary>The conversion from raw to physical values, if any.</summary>
    public Mdf4Conversion? Conversion { get; }

    /// <summary>The source of this channel, if any.</summary>
    public Mdf4SourceInformation? Source { get; }

    /// <summary>The members of a structure (composition), in file order.</summary>
    public IReadOnlyList<Mdf4Channel> Children => _children;

    /// <summary>True if the channel is an array (CA composition), whose elements this library does not expand.</summary>
    public bool IsArray { get; }

    /// <summary>True if the raw value is a number (integer or float).</summary>
    public bool IsNumeric => ChannelType is Mdf4ChannelType.VirtualMaster or Mdf4ChannelType.VirtualData
        || DataType <= Mdf4DataType.FloatBe;

    /// <summary>
    /// Reads every sample as a physical number. Invalid samples are <see cref="double.NaN"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel is not numeric, or its conversion produces texts.</exception>
    /// <exception cref="NotSupportedException">The conversion type is not supported.</exception>
    public double[] ReadDoubles()
    {
        if (!IsNumeric)
        {
            throw new InvalidOperationException($"Channel '{Name}' holds {DataType} values; read it with ReadValues().");
        }

        if (Conversion is { IsSupported: false })
        {
            throw new NotSupportedException($"Channel '{Name}' uses a {Conversion.Type} conversion, which is not supported.");
        }

        if (Conversion is { ProducesText: true })
        {
            throw new InvalidOperationException($"Channel '{Name}' converts to texts; read it with ReadValues().");
        }

        if (Conversion is { TakesText: true })
        {
            throw Mdf4FormatException.At(Offset, $"Channel '{Name}' is numeric but its conversion takes texts");
        }

        var result = new List<double>();
        bool integer = IsInteger;
        long index = 0;
        foreach (ArraySegment<byte> record in ChannelGroup.DataGroup.EnumerateRecords(ChannelGroup))
        {
            ReadOnlySpan<byte> span = record.AsSpan();
            if (IsInvalid(span))
            {
                result.Add(double.NaN);
            }
            else
            {
                double raw = DecodeRawNumber(span, index);
                result.Add(Conversion is null ? raw : Conversion.ConvertToDouble(raw, integer));
            }

            index++;
        }

        return result.ToArray();
    }

    /// <summary>
    /// Reads every sample as a physical value: a <see cref="double"/> for numbers, a
    /// <see cref="string"/> for texts and text conversions, a <see cref="byte"/> array for byte
    /// arrays, a <see cref="DateTime"/> for CANopen date and time. Invalid samples are null.
    /// </summary>
    public object?[] ReadValues() => Read(physical: true);

    /// <summary>
    /// Reads every sample without applying the conversion: <see cref="long"/> or
    /// <see cref="ulong"/> for integers, <see cref="double"/> for floats, otherwise as
    /// <see cref="ReadValues"/>. Invalid samples are null.
    /// </summary>
    public object?[] ReadRawValues() => Read(physical: false);

    internal bool IsInteger => DataType <= Mdf4DataType.SignedIntegerBe
        || ChannelType is Mdf4ChannelType.VirtualMaster or Mdf4ChannelType.VirtualData;

    internal bool IsInvalid(ReadOnlySpan<byte> record)
    {
        if ((Flags & Mdf4ChannelFlags.AllValuesInvalid) != 0)
        {
            return true;
        }

        if ((Flags & Mdf4ChannelFlags.InvalidationBitValid) == 0 || ChannelGroup.InvalidationBytes == 0)
        {
            return false;
        }

        long byteIndex = ChannelGroup.DataBytes + (InvalidationBitPosition >> 3);
        if (byteIndex >= record.Length)
        {
            throw Mdf4FormatException.At(Offset, $"Invalidation bit of channel '{Name}' lies outside the record");
        }

        return ((record[(int)byteIndex] >> (int)(InvalidationBitPosition & 7)) & 1) != 0;
    }

    internal object? DecodePhysical(ReadOnlySpan<byte> record, long index)
    {
        object? raw = DecodeRaw(record, index);
        if (Conversion is null || raw is null)
        {
            return raw switch
            {
                long l => (double)l,
                ulong u => (double)u,
                _ => raw,
            };
        }

        switch (raw)
        {
            case string text:
                return Conversion.Type is Mdf4ConversionType.TextToValue or Mdf4ConversionType.TextToText
                    ? Conversion.Convert(text)
                    : text;
            case long or ulong or double when Conversion.TakesText:
                throw Mdf4FormatException.At(Offset, $"Channel '{Name}' is numeric but its conversion takes texts");
            case long l:
                return Conversion.Convert(l, rawIsInteger: true);
            case ulong u:
                return Conversion.Convert(u, rawIsInteger: true);
            case double d:
                return Conversion.Convert(d, rawIsInteger: false);
            default:
                return raw;
        }
    }

    internal double DecodeRawNumber(ReadOnlySpan<byte> record, long index)
    {
        switch (ChannelType)
        {
            case Mdf4ChannelType.VirtualMaster:
            case Mdf4ChannelType.VirtualData:
                return index;
        }

        int bits = (int)Math.Min(BitCount, 64);
        switch (DataType)
        {
            case Mdf4DataType.UnsignedIntegerLe:
            case Mdf4DataType.UnsignedIntegerBe:
                CheckInteger(record);
                return BitField.ReadUnsigned(record, (int)ByteOffset, BitOffset, bits, DataType == Mdf4DataType.UnsignedIntegerBe);
            case Mdf4DataType.SignedIntegerLe:
            case Mdf4DataType.SignedIntegerBe:
                CheckInteger(record);
                return BitField.ReadSigned(record, (int)ByteOffset, BitOffset, bits, DataType == Mdf4DataType.SignedIntegerBe);
            case Mdf4DataType.FloatLe:
            case Mdf4DataType.FloatBe:
                CheckFits(record, bits / 8);
                return BitField.ReadFloat(record, (int)ByteOffset, bits, DataType == Mdf4DataType.FloatBe);
            default:
                throw Mdf4FormatException.At(Offset, $"Channel '{Name}' holds {DataType} values where a number is required");
        }
    }

    internal object? DecodeRaw(ReadOnlySpan<byte> record, long index)
    {
        switch (ChannelType)
        {
            case Mdf4ChannelType.VirtualMaster:
            case Mdf4ChannelType.VirtualData:
                return (ulong)index;
            case Mdf4ChannelType.VariableLength:
                return DecodeBytes(SignalDataAt(record));
            case Mdf4ChannelType.MaximumLength:
                return DecodeBytes(MaximumLengthBytes(record, index));
        }

        switch (DataType)
        {
            case Mdf4DataType.UnsignedIntegerLe:
            case Mdf4DataType.UnsignedIntegerBe:
                CheckInteger(record);
                return BitField.ReadUnsigned(record, (int)ByteOffset, BitOffset, (int)BitCount, DataType == Mdf4DataType.UnsignedIntegerBe);
            case Mdf4DataType.SignedIntegerLe:
            case Mdf4DataType.SignedIntegerBe:
                CheckInteger(record);
                return BitField.ReadSigned(record, (int)ByteOffset, BitOffset, (int)BitCount, DataType == Mdf4DataType.SignedIntegerBe);
            case Mdf4DataType.FloatLe:
            case Mdf4DataType.FloatBe:
                CheckFits(record, (int)(BitCount / 8));
                return BitField.ReadFloat(record, (int)ByteOffset, (int)BitCount, DataType == Mdf4DataType.FloatBe);
            default:
                return DecodeBytes(FieldBytes(record));
        }
    }

    internal void ResolveDataLink()
    {
        if (_dataLink == 0)
        {
            return;
        }

        BlockReader reader = ChannelGroup.DataGroup.File.Reader;
        if (ChannelType == Mdf4ChannelType.VariableLength)
        {
            BlockHeader header = reader.ReadHeader(_dataLink);
            if (header.Id == "##CG")
            {
                _signalDataGroup = ChannelGroup.DataGroup.FindByOffset(_dataLink)
                    ?? throw Mdf4FormatException.At(Offset, $"VLSD channel '{Name}' refers to a channel group of another data group");
                if (!_signalDataGroup.IsVariableLengthSignalData)
                {
                    throw Mdf4FormatException.At(Offset, $"VLSD channel '{Name}' refers to a channel group that is not a VLSD group");
                }
            }
        }
        else if (ChannelType == Mdf4ChannelType.MaximumLength)
        {
            _lengthChannel = ChannelGroup.ChannelAt(_dataLink);
        }
    }

    private object?[] Read(bool physical)
    {
        var result = new List<object?>();
        long index = 0;
        foreach (ArraySegment<byte> record in ChannelGroup.DataGroup.EnumerateRecords(ChannelGroup))
        {
            ReadOnlySpan<byte> span = record.AsSpan();
            if (IsInvalid(span))
            {
                result.Add(null);
            }
            else
            {
                object? raw = physical ? DecodePhysical(span, index) : DecodeRaw(span, index);
                result.Add(raw);
            }

            index++;
        }

        return result.ToArray();
    }

    private void CheckInteger(ReadOnlySpan<byte> record)
    {
        if (BitCount == 0 || BitCount > 64 || BitOffset > 7)
        {
            throw Mdf4FormatException.At(Offset, $"Integer channel '{Name}' has an invalid bit layout ({BitOffset}+{BitCount})");
        }

        CheckFits(record, BitField.ByteSpan(BitOffset, (int)BitCount));
    }

    private void CheckFits(ReadOnlySpan<byte> record, int bytes)
    {
        if (BitOffset > 7 || bytes < 0 || ByteOffset > int.MaxValue || (long)ByteOffset + bytes > record.Length)
        {
            throw Mdf4FormatException.At(Offset, $"Channel '{Name}' lies outside its record ({ByteOffset}+{bytes} > {record.Length})");
        }

        if (DataType is Mdf4DataType.FloatLe or Mdf4DataType.FloatBe && BitCount is not (16 or 32 or 64))
        {
            throw Mdf4FormatException.At(Offset, $"Float channel '{Name}' has {BitCount} bits");
        }
    }

    private ReadOnlySpan<byte> FieldBytes(ReadOnlySpan<byte> record)
    {
        int bytes = (int)Math.Min(BitCount / 8, int.MaxValue);
        CheckFits(record, bytes);
        return record.Slice((int)ByteOffset, bytes);
    }

    private ReadOnlySpan<byte> MaximumLengthBytes(ReadOnlySpan<byte> record, long index)
    {
        ReadOnlySpan<byte> field = FieldBytes(record);
        if (_lengthChannel is null)
        {
            return field;
        }

        double length = _lengthChannel.DecodeRawNumber(record, index);
        if (length < 0 || double.IsNaN(length))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return field.Slice(0, (int)Math.Min(field.Length, length));
    }

    private ReadOnlySpan<byte> SignalDataAt(ReadOnlySpan<byte> record)
    {
        if (BitCount is 0 or > 64 || BitOffset != 0)
        {
            throw Mdf4FormatException.At(Offset, $"VLSD channel '{Name}' has an invalid offset field ({BitOffset}+{BitCount})");
        }

        CheckFits(record, BitField.ByteSpan(0, (int)BitCount));
        ulong offset = BitCount == 64
            ? BinaryPrimitives.ReadUInt64LittleEndian(record.Slice((int)ByteOffset, 8))
            : BitField.ReadUnsigned(record, (int)ByteOffset, BitOffset, (int)Math.Max(1, BitCount), bigEndian: false);
        byte[] data = LoadSignalData();
        if (offset > (ulong)data.Length || (ulong)data.Length - offset < 4)
        {
            throw Mdf4FormatException.At(Offset, $"VLSD channel '{Name}' points at offset {offset}, outside its {data.Length} bytes of signal data");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset, 4));
        if (length > (ulong)data.Length - offset - 4)
        {
            throw Mdf4FormatException.At(Offset, $"VLSD channel '{Name}' has a value running past the end of its signal data");
        }

        return data.AsSpan((int)offset + 4, (int)length);
    }

    private byte[] LoadSignalData()
    {
        if (_signalData is not null)
        {
            return _signalData;
        }

        long max = ChannelGroup.DataGroup.File.Reader.Options.MaxSignalDataSize;
        using var buffer = new MemoryStream();
        IEnumerable<ArraySegment<byte>> chunks = _signalDataGroup is not null
            ? ChannelGroup.DataGroup.EnumerateRecords(_signalDataGroup)
            : DataChain.Enumerate(ChannelGroup.DataGroup.File.Reader, _dataLink, "##SD");
        foreach (ArraySegment<byte> chunk in chunks)
        {
            if (buffer.Length + chunk.Count > max || buffer.Length + chunk.Count > int.MaxValue)
            {
                throw Mdf4FormatException.At(Offset, $"Signal data of channel '{Name}' exceeds MaxSignalDataSize");
            }

            buffer.Write(chunk.Array!, chunk.Offset, chunk.Count);
        }

        _signalData = buffer.ToArray();
        return _signalData;
    }

    private object? DecodeBytes(ReadOnlySpan<byte> bytes)
    {
        switch (DataType)
        {
            case Mdf4DataType.StringLatin1:
                return Latin1.GetString(TrimAtZero(bytes, 1).ToArray());
            case Mdf4DataType.StringUtf8:
                return Encoding.UTF8.GetString(TrimAtZero(bytes, 1).ToArray());
            case Mdf4DataType.StringUtf16Le:
                return Encoding.Unicode.GetString(TrimAtZero(bytes, 2).ToArray());
            case Mdf4DataType.StringUtf16Be:
                return Encoding.BigEndianUnicode.GetString(TrimAtZero(bytes, 2).ToArray());
            case Mdf4DataType.CanOpenDate:
                return CanOpenDate(bytes);
            case Mdf4DataType.CanOpenTime:
                return CanOpenTime(bytes);
            default:
                return bytes.ToArray();
        }
    }

    private static ReadOnlySpan<byte> TrimAtZero(ReadOnlySpan<byte> bytes, int width)
    {
        for (int i = 0; i + width <= bytes.Length; i += width)
        {
            bool zero = true;
            for (int k = 0; k < width; k++)
            {
                zero &= bytes[i + k] == 0;
            }

            if (zero)
            {
                return bytes.Slice(0, i);
            }
        }

        return bytes.Slice(0, bytes.Length - (bytes.Length % width));
    }

    private static DateTime? CanOpenDate(ReadOnlySpan<byte> b)
    {
        if (b.Length < 7)
        {
            return null;
        }

        int ms = BinaryPrimitives.ReadUInt16LittleEndian(b);
        int minute = b[2] & 0x3F;
        int hour = b[3] & 0x1F;
        int day = b[4] & 0x1F;
        int month = b[5] & 0x3F;
        int year = 2000 + (b[6] & 0x7F);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || ms > 59999)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified).AddMilliseconds(ms);
    }

    private static DateTime? CanOpenTime(ReadOnlySpan<byte> b)
    {
        if (b.Length < 6)
        {
            return null;
        }

        uint ms = BinaryPrimitives.ReadUInt32LittleEndian(b) & 0x0FFFFFFF;
        ushort days = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4));
        if (ms >= 86_400_000)
        {
            return null;
        }

        return new DateTime(1984, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(days).AddMilliseconds(ms);
    }
}
