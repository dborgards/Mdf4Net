using System;

namespace Mdf4Net;

/// <summary>The MDF format version written to, or read from, the identification block.</summary>
public enum Mdf4Version
{
    /// <summary>MDF 4.10. Adds DZ/DL/HL compression to 4.0; the most widely supported 4.x version.</summary>
    V410 = 410,

    /// <summary>MDF 4.20.</summary>
    V420 = 420,
}

/// <summary>How the writer stores record data (the DT blocks of a data group).</summary>
public enum Mdf4Compression
{
    /// <summary>Plain DT blocks. Fastest, largest.</summary>
    None = 0,

    /// <summary>DZ blocks, zlib deflate of the record bytes as they are.</summary>
    Deflate = 1,

    /// <summary>
    /// DZ blocks, records transposed (byte columns first) before deflate. Usually compresses
    /// measurement data noticeably better than plain deflate, because a byte column of a slowly
    /// changing signal is highly repetitive.
    /// </summary>
    TransposedDeflate = 2,
}

/// <summary>The kind of a channel (<c>cn_type</c>).</summary>
public enum Mdf4ChannelType : byte
{
    /// <summary>A value stored in the fixed-length record.</summary>
    FixedLength = 0,

    /// <summary>Variable-length signal data: the record holds an offset into signal data.</summary>
    VariableLength = 1,

    /// <summary>The master channel of the group (time, angle, distance or index).</summary>
    Master = 2,

    /// <summary>A master channel computed from the record index and the conversion; not stored.</summary>
    VirtualMaster = 3,

    /// <summary>A synchronisation channel referring to an attachment.</summary>
    Synchronization = 4,

    /// <summary>A byte array whose valid length is given by another channel.</summary>
    MaximumLength = 5,

    /// <summary>A data channel computed from the record index and the conversion; not stored.</summary>
    VirtualData = 6,
}

/// <summary>What a master or synchronisation channel measures (<c>cn_sync_type</c>).</summary>
public enum Mdf4SyncType : byte
{
    /// <summary>Not a master or synchronisation channel.</summary>
    None = 0,

    /// <summary>Time, in seconds.</summary>
    Time = 1,

    /// <summary>Angle, in radians.</summary>
    Angle = 2,

    /// <summary>Distance, in metres.</summary>
    Distance = 3,

    /// <summary>A zero-based sample index.</summary>
    Index = 4,
}

/// <summary>How a channel's raw value is encoded (<c>cn_data_type</c>).</summary>
public enum Mdf4DataType : byte
{
    /// <summary>Unsigned integer, little endian (Intel).</summary>
    UnsignedIntegerLe = 0,

    /// <summary>Unsigned integer, big endian (Motorola).</summary>
    UnsignedIntegerBe = 1,

    /// <summary>Two's complement signed integer, little endian.</summary>
    SignedIntegerLe = 2,

    /// <summary>Two's complement signed integer, big endian.</summary>
    SignedIntegerBe = 3,

    /// <summary>IEEE 754 floating point (16, 32 or 64 bit), little endian.</summary>
    FloatLe = 4,

    /// <summary>IEEE 754 floating point (16, 32 or 64 bit), big endian.</summary>
    FloatBe = 5,

    /// <summary>Single-byte string, ISO-8859-1, zero-terminated if shorter than the field.</summary>
    StringLatin1 = 6,

    /// <summary>UTF-8 string, zero-terminated if shorter than the field.</summary>
    StringUtf8 = 7,

    /// <summary>UTF-16 little endian string.</summary>
    StringUtf16Le = 8,

    /// <summary>UTF-16 big endian string.</summary>
    StringUtf16Be = 9,

    /// <summary>Opaque byte array.</summary>
    ByteArray = 10,

    /// <summary>A MIME sample (byte array with a MIME type in the unit).</summary>
    MimeSample = 11,

    /// <summary>A MIME stream.</summary>
    MimeStream = 12,

    /// <summary>CANopen DATE (7 bytes).</summary>
    CanOpenDate = 13,

    /// <summary>CANopen TIME_OF_DAY (6 bytes).</summary>
    CanOpenTime = 14,

    /// <summary>Complex number, two floats, little endian (MDF 4.20).</summary>
    ComplexLe = 15,

    /// <summary>Complex number, two floats, big endian (MDF 4.20).</summary>
    ComplexBe = 16,
}

/// <summary>Channel flags (<c>cn_flags</c>).</summary>
[Flags]
public enum Mdf4ChannelFlags : uint
{
    /// <summary>No flag set.</summary>
    None = 0,

    /// <summary>Every value of the channel is invalid.</summary>
    AllValuesInvalid = 1u << 0,

    /// <summary>The invalidation bit position is valid.</summary>
    InvalidationBitValid = 1u << 1,

    /// <summary>The precision field is valid.</summary>
    PrecisionValid = 1u << 2,

    /// <summary>The value range is valid.</summary>
    ValueRangeValid = 1u << 3,

    /// <summary>The limit range is valid.</summary>
    LimitRangeValid = 1u << 4,

    /// <summary>The extended limit range is valid.</summary>
    ExtendedLimitRangeValid = 1u << 5,

    /// <summary>The signal is discrete (no interpolation between samples).</summary>
    DiscreteValue = 1u << 6,

    /// <summary>The channel is a calibration object.</summary>
    Calibration = 1u << 7,

    /// <summary>The channel was calculated.</summary>
    Calculated = 1u << 8,

    /// <summary>The channel is virtual.</summary>
    Virtual = 1u << 9,

    /// <summary>The channel carries bus event data (ASAM bus logging).</summary>
    BusEvent = 1u << 10,

    /// <summary>The values are strictly monotonous.</summary>
    StrictlyMonotonous = 1u << 11,

    /// <summary>A default X axis is given.</summary>
    DefaultX = 1u << 12,
}

/// <summary>Channel group flags (<c>cg_flags</c>).</summary>
[Flags]
public enum Mdf4ChannelGroupFlags : ushort
{
    /// <summary>No flag set.</summary>
    None = 0,

    /// <summary>The group holds variable-length signal data of a VLSD channel, not channels.</summary>
    VariableLengthSignalData = 1 << 0,

    /// <summary>The group holds bus events (ASAM bus logging).</summary>
    BusEvent = 1 << 1,

    /// <summary>The group holds plain bus events only, no signal values.</summary>
    PlainBusEvent = 1 << 2,

    /// <summary>A remote master channel group is referenced (MDF 4.20).</summary>
    RemoteMaster = 1 << 3,

    /// <summary>The group holds event signals (MDF 4.20).</summary>
    EventSignal = 1 << 4,
}

/// <summary>The kind of a source (<c>si_type</c>).</summary>
public enum Mdf4SourceType : byte
{
    /// <summary>Any other source.</summary>
    Other = 0,

    /// <summary>An ECU.</summary>
    Ecu = 1,

    /// <summary>A bus.</summary>
    Bus = 2,

    /// <summary>An I/O device.</summary>
    IO = 3,

    /// <summary>A software tool.</summary>
    Tool = 4,

    /// <summary>A user interaction.</summary>
    User = 5,
}

/// <summary>The bus a source is attached to (<c>si_bus_type</c>).</summary>
public enum Mdf4BusType : byte
{
    /// <summary>No bus.</summary>
    None = 0,

    /// <summary>A bus not listed here.</summary>
    Other = 1,

    /// <summary>CAN and CAN FD.</summary>
    Can = 2,

    /// <summary>LIN.</summary>
    Lin = 3,

    /// <summary>MOST.</summary>
    Most = 4,

    /// <summary>FlexRay.</summary>
    FlexRay = 5,

    /// <summary>K-Line.</summary>
    KLine = 6,

    /// <summary>Ethernet.</summary>
    Ethernet = 7,

    /// <summary>USB.</summary>
    Usb = 8,
}

/// <summary>The kind of a channel conversion (<c>cc_type</c>).</summary>
public enum Mdf4ConversionType : byte
{
    /// <summary>Physical value equals the raw value.</summary>
    Identity = 0,

    /// <summary><c>phys = p2 * raw + p1</c>.</summary>
    Linear = 1,

    /// <summary><c>phys = (p1 x² + p2 x + p3) / (p4 x² + p5 x + p6)</c>.</summary>
    Rational = 2,

    /// <summary>A formula given as text.</summary>
    Algebraic = 3,

    /// <summary>Value-to-value table with linear interpolation.</summary>
    ValueToValueInterpolated = 4,

    /// <summary>Value-to-value table, nearest key.</summary>
    ValueToValue = 5,

    /// <summary>Value range to value table.</summary>
    ValueRangeToValue = 6,

    /// <summary>Value to text (or to a nested conversion).</summary>
    ValueToText = 7,

    /// <summary>Value range to text (or to a nested conversion).</summary>
    ValueRangeToText = 8,

    /// <summary>Text to value.</summary>
    TextToValue = 9,

    /// <summary>Text to text.</summary>
    TextToText = 10,

    /// <summary>Bit field to text (MDF 4.20).</summary>
    BitfieldText = 11,
}
