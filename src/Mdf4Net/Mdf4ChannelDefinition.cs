using System;
using System.Collections.Generic;
using System.Linq;

namespace Mdf4Net;

/// <summary>
/// A channel to write: its name, raw encoding and metadata. The same instance is the handle used
/// to set the channel's value in a record (<see cref="Mdf4RecordBuilder"/>).
/// </summary>
/// <remarks>
/// Create common channels with the static factories (<see cref="UInt16(string)"/>,
/// <see cref="Float64(string)"/>, <see cref="VariableLengthString(string, Mdf4DataType)"/>, …) and set the
/// optional metadata with an object initializer. Unless <see cref="ByteOffset"/> is given, the
/// writer places channels one after another, byte aligned, in the order they were added.
/// </remarks>
public sealed class Mdf4ChannelDefinition
{
    /// <summary>Creates a channel with an explicit encoding.</summary>
    /// <param name="name">The channel name.</param>
    /// <param name="dataType">The raw encoding.</param>
    /// <param name="bitCount">
    /// Bits of the raw value: 1..64 for integers, 16/32/64 for floats, a multiple of eight for
    /// strings and byte arrays.
    /// </param>
    public Mdf4ChannelDefinition(string name, Mdf4DataType dataType, int bitCount)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A channel needs a name.", nameof(name));
        }

        Name = name;
        DataType = dataType;
        BitCount = bitCount;
    }

    /// <summary>The channel name.</summary>
    public string Name { get; }

    /// <summary>The raw encoding.</summary>
    public Mdf4DataType DataType { get; }

    /// <summary>Bits of the raw value. For a variable-length channel this is 64 (the offset).</summary>
    public int BitCount { get; }

    /// <summary>
    /// The kind of channel: <see cref="Mdf4ChannelType.FixedLength"/> (default),
    /// <see cref="Mdf4ChannelType.Master"/> or <see cref="Mdf4ChannelType.VariableLength"/>.
    /// </summary>
    public Mdf4ChannelType ChannelType { get; init; } = Mdf4ChannelType.FixedLength;

    /// <summary>What a master channel measures. Required for <see cref="Mdf4ChannelType.Master"/>.</summary>
    public Mdf4SyncType SyncType { get; init; } = Mdf4SyncType.None;

    /// <summary>The physical unit.</summary>
    public string? Unit { get; init; }

    /// <summary>The channel comment.</summary>
    public string? Comment { get; init; }

    /// <summary>The conversion from raw to physical values.</summary>
    public Mdf4ConversionDefinition? Conversion { get; init; }

    /// <summary>The channel's own source, if it differs from the group's.</summary>
    public Mdf4SourceDefinition? Source { get; init; }

    /// <summary>
    /// Additional flags, e.g. <see cref="Mdf4ChannelFlags.DiscreteValue"/> or
    /// <see cref="Mdf4ChannelFlags.BusEvent"/>. Range and precision flags are set automatically.
    /// </summary>
    public Mdf4ChannelFlags Flags { get; init; }

    /// <summary>Decimal places for display.</summary>
    public byte? Precision { get; init; }

    /// <summary>Lower end of the physical value range.</summary>
    public double? MinimumValue { get; init; }

    /// <summary>Upper end of the physical value range.</summary>
    public double? MaximumValue { get; init; }

    /// <summary>
    /// An explicit byte offset in the record. Leave null to let the writer place the channel.
    /// Composition members with explicit offsets use record-absolute offsets.
    /// </summary>
    public int? ByteOffset { get; init; }

    /// <summary>An explicit bit offset (0..7) within the first byte; requires <see cref="ByteOffset"/>.</summary>
    public int BitOffset { get; init; }

    /// <summary>
    /// The members of a structure: this channel becomes a byte array spanning them, and the
    /// members are written as its composition.
    /// </summary>
    public IReadOnlyList<Mdf4ChannelDefinition> Members
    {
        get => _members;

        // A copy: the caller's list could be changed after the channel was validated.
        init => _members = (value ?? throw new ArgumentNullException(nameof(value))).ToArray();
    }

    private Mdf4ChannelDefinition[] _members = Array.Empty<Mdf4ChannelDefinition>();

    internal bool IsVariableLength => ChannelType == Mdf4ChannelType.VariableLength;

    internal bool IsInteger => DataType <= Mdf4DataType.SignedIntegerBe;

    internal bool IsSigned => DataType is Mdf4DataType.SignedIntegerLe or Mdf4DataType.SignedIntegerBe;

    internal bool IsFloat => DataType is Mdf4DataType.FloatLe or Mdf4DataType.FloatBe;

    internal bool IsBigEndian => DataType is Mdf4DataType.UnsignedIntegerBe or Mdf4DataType.SignedIntegerBe or Mdf4DataType.FloatBe;

    /// <summary>A master channel of time stamps: 64-bit float, seconds since <c>StartTime</c>.</summary>
    public static Mdf4ChannelDefinition Time(string name = "t")
        => new(name, Mdf4DataType.FloatLe, 64) { ChannelType = Mdf4ChannelType.Master, SyncType = Mdf4SyncType.Time, Unit = "s" };

    /// <summary>A single-bit unsigned channel (a flag).</summary>
    public static Mdf4ChannelDefinition Bit(string name) => new(name, Mdf4DataType.UnsignedIntegerLe, 1);

    /// <summary>An unsigned 8-bit integer channel.</summary>
    public static Mdf4ChannelDefinition UInt8(string name) => new(name, Mdf4DataType.UnsignedIntegerLe, 8);

    /// <summary>An unsigned 16-bit integer channel, little endian.</summary>
    public static Mdf4ChannelDefinition UInt16(string name) => new(name, Mdf4DataType.UnsignedIntegerLe, 16);

    /// <summary>An unsigned 32-bit integer channel, little endian.</summary>
    public static Mdf4ChannelDefinition UInt32(string name) => new(name, Mdf4DataType.UnsignedIntegerLe, 32);

    /// <summary>An unsigned 64-bit integer channel, little endian.</summary>
    public static Mdf4ChannelDefinition UInt64(string name) => new(name, Mdf4DataType.UnsignedIntegerLe, 64);

    /// <summary>A signed 8-bit integer channel.</summary>
    public static Mdf4ChannelDefinition Int8(string name) => new(name, Mdf4DataType.SignedIntegerLe, 8);

    /// <summary>A signed 16-bit integer channel, little endian.</summary>
    public static Mdf4ChannelDefinition Int16(string name) => new(name, Mdf4DataType.SignedIntegerLe, 16);

    /// <summary>A signed 32-bit integer channel, little endian.</summary>
    public static Mdf4ChannelDefinition Int32(string name) => new(name, Mdf4DataType.SignedIntegerLe, 32);

    /// <summary>A signed 64-bit integer channel, little endian.</summary>
    public static Mdf4ChannelDefinition Int64(string name) => new(name, Mdf4DataType.SignedIntegerLe, 64);

    /// <summary>A 32-bit float channel, little endian.</summary>
    public static Mdf4ChannelDefinition Float32(string name) => new(name, Mdf4DataType.FloatLe, 32);

    /// <summary>A 64-bit float channel, little endian.</summary>
    public static Mdf4ChannelDefinition Float64(string name) => new(name, Mdf4DataType.FloatLe, 64);

    /// <summary>A fixed-length byte array stored in the record.</summary>
    public static Mdf4ChannelDefinition Bytes(string name, int length) => new(name, Mdf4DataType.ByteArray, checked(length * 8));

    /// <summary>A fixed-length string field stored in the record, zero-padded.</summary>
    /// <param name="name">The channel name.</param>
    /// <param name="byteLength">The field size in bytes.</param>
    /// <param name="encoding">One of the string data types; UTF-8 by default.</param>
    public static Mdf4ChannelDefinition String(string name, int byteLength, Mdf4DataType encoding = Mdf4DataType.StringUtf8)
        => new(name, RequireString(encoding), checked(byteLength * 8));

    /// <summary>A string of any length, stored as variable-length signal data.</summary>
    public static Mdf4ChannelDefinition VariableLengthString(string name, Mdf4DataType encoding = Mdf4DataType.StringUtf8)
        => new(name, RequireString(encoding), 64) { ChannelType = Mdf4ChannelType.VariableLength };

    /// <summary>A byte array of any length, stored as variable-length signal data.</summary>
    public static Mdf4ChannelDefinition VariableLengthBytes(string name)
        => new(name, Mdf4DataType.ByteArray, 64) { ChannelType = Mdf4ChannelType.VariableLength };

    internal void Validate()
    {
        switch (ChannelType)
        {
            case Mdf4ChannelType.FixedLength:
            case Mdf4ChannelType.Master:
            case Mdf4ChannelType.VariableLength:
                break;
            default:
                throw new ArgumentException($"Channel '{Name}': writing {ChannelType} channels is not supported.");
        }

        if (ChannelType == Mdf4ChannelType.Master && SyncType is < Mdf4SyncType.Time or > Mdf4SyncType.Index)
        {
            throw new ArgumentException($"Master channel '{Name}' needs a SyncType of Time, Angle, Distance or Index.");
        }

        if (ChannelType == Mdf4ChannelType.Master
            && (!(IsInteger || IsFloat) || Members.Count > 0 || Conversion is { Type: Mdf4ConversionType.ValueToText or Mdf4ConversionType.ValueRangeToText or Mdf4ConversionType.TextToValue or Mdf4ConversionType.TextToText or Mdf4ConversionType.BitfieldText }))
        {
            throw new ArgumentException($"Master channel '{Name}' has to be a number without a conversion to or from texts.");
        }

        if (IsVariableLength)
        {
            if (DataType is not (Mdf4DataType.ByteArray or Mdf4DataType.StringLatin1 or Mdf4DataType.StringUtf8 or Mdf4DataType.StringUtf16Le or Mdf4DataType.StringUtf16Be or Mdf4DataType.MimeSample or Mdf4DataType.MimeStream))
            {
                throw new ArgumentException($"Variable-length channel '{Name}' must hold strings or bytes.");
            }

            if (BitCount != 64 || Members.Count > 0)
            {
                throw new ArgumentException($"Variable-length channel '{Name}' must have 64 bits and no members.");
            }
        }
        else if (Members.Count > 0)
        {
            if (DataType != Mdf4DataType.ByteArray)
            {
                throw new ArgumentException($"Structure channel '{Name}' must be a byte array.");
            }
        }
        else if (IsInteger)
        {
            if (BitCount is < 1 or > 64)
            {
                throw new ArgumentException($"Integer channel '{Name}' must have 1..64 bits.");
            }
        }
        else if (IsFloat)
        {
            if (BitCount is not (16 or 32 or 64))
            {
                throw new ArgumentException($"Float channel '{Name}' must have 16, 32 or 64 bits.");
            }
        }
        else if (DataType > Mdf4DataType.MimeStream)
        {
            throw new ArgumentException($"Channel '{Name}': writing {DataType} values is not supported.");
        }
        else if (BitCount <= 0 || BitCount % 8 != 0)
        {
            throw new ArgumentException($"Channel '{Name}' must have a positive multiple of 8 bits.");
        }

        if (BitOffset is < 0 or > 7)
        {
            throw new ArgumentException($"Channel '{Name}': BitOffset must be 0..7.");
        }

        if (BitOffset != 0 && (ByteOffset is null || !IsInteger))
        {
            throw new ArgumentException($"Channel '{Name}': a bit offset needs an explicit ByteOffset and an integer type.");
        }

        if (ByteOffset is < 0)
        {
            throw new ArgumentException($"Channel '{Name}': ByteOffset must not be negative.");
        }

        if (Conversion is not null && !(IsInteger || IsFloat))
        {
            throw new ArgumentException($"Channel '{Name}': only numeric channels can have a conversion.");
        }

        foreach (Mdf4ChannelDefinition member in Members)
        {
            if (member.ChannelType != Mdf4ChannelType.FixedLength)
            {
                throw new ArgumentException($"Structure member '{member.Name}' must be a fixed-length channel.");
            }

            member.Validate();
        }
    }

    private static Mdf4DataType RequireString(Mdf4DataType encoding)
        => encoding is Mdf4DataType.StringLatin1 or Mdf4DataType.StringUtf8 or Mdf4DataType.StringUtf16Le or Mdf4DataType.StringUtf16Be
            ? encoding
            : throw new ArgumentException("Use one of the string data types.", nameof(encoding));
}
