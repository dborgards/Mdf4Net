using System;
using System.Collections.Generic;
using System.Linq;

namespace Mdf4Net.BusLogging;

/// <summary>Options for a CAN data frame channel group.</summary>
public sealed class CanBusLoggingOptions
{
    /// <summary>The acquisition name of the channel group. Default <c>CAN_DataFrame</c>.</summary>
    public string GroupName { get; init; } = "CAN_DataFrame";

    /// <summary>The bus network name recorded in the source information. Default <c>CAN</c>.</summary>
    public string NetworkName { get; init; } = "CAN";

    /// <summary>
    /// The size of the stored payload field: 8 for classic CAN only, 64 to allow CAN FD frames.
    /// Default 64. Shorter payloads are zero-padded; <c>DataLength</c> records the real length.
    /// </summary>
    public int MaxDataLength { get; init; } = 64;
}

/// <summary>
/// ASAM MDF bus logging for CAN: writes and reads <c>CAN_DataFrame</c> channel groups as tools
/// such as asammdf, CANape and CANoe expect them (bus event group, <c>t</c> master, structure
/// channel <c>CAN_DataFrame</c> with members <c>BusChannel</c>, <c>ID</c>, <c>IDE</c>,
/// <c>DLC</c>, <c>DataLength</c>, <c>DataBytes</c>, <c>Dir</c>, <c>EDL</c>, <c>BRS</c>,
/// <c>ESI</c>).
/// </summary>
public static class CanBusLogging
{
    internal const string FrameChannel = "CAN_DataFrame";

    /// <summary>Adds a CAN data frame channel group to the writer.</summary>
    public static CanDataFrameWriter AddCanDataFrameGroup(this Mdf4Writer writer, CanBusLoggingOptions? options = null)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        options ??= new CanBusLoggingOptions();
        if (options.MaxDataLength is not (8 or 64))
        {
            throw new ArgumentException("MaxDataLength must be 8 or 64.", nameof(options));
        }

        return new CanDataFrameWriter(writer, options);
    }

    /// <summary>True if the group is an ASAM bus logging group of CAN data frames.</summary>
    public static bool IsCanDataFrameGroup(this Mdf4ChannelGroup group)
    {
        if (group is null)
        {
            throw new ArgumentNullException(nameof(group));
        }

        return group.IsBusEvent
            && (group.Source is null || group.Source.BusType is Mdf4BusType.Can or Mdf4BusType.None)
            && group.Channels.Any(c => c.Name == FrameChannel)
            && group.FindChannel(FrameChannel + ".ID") is not null
            && group.FindChannel(FrameChannel + ".DataBytes") is not null;
    }

    /// <summary>
    /// Reads the CAN data frames of one bus logging channel group, in record order. A record whose
    /// identifier, payload or time stamp is marked invalid is not a frame and is skipped; an
    /// optional field marked invalid reads as if the group did not have it.
    /// </summary>
    /// <exception cref="ArgumentException">The group is not a CAN data frame group.</exception>
    public static IEnumerable<CanDataFrame> ReadCanDataFrames(this Mdf4ChannelGroup group)
    {
        if (!group.IsCanDataFrameGroup())
        {
            throw new ArgumentException("The channel group is not a CAN_DataFrame bus logging group.", nameof(group));
        }

        return CanDataFrameReader.Read(group);
    }

    /// <summary>
    /// Reads the CAN data frames of every CAN bus logging group of the file, merged in time
    /// stamp order (stable for equal time stamps). Records with invalid frame fields are treated
    /// as by <see cref="ReadCanDataFrames(Mdf4ChannelGroup)"/>.
    /// </summary>
    public static IReadOnlyList<CanDataFrame> ReadCanDataFrames(this Mdf4File file)
    {
        if (file is null)
        {
            throw new ArgumentNullException(nameof(file));
        }

        return file.ChannelGroups
            .Where(g => g.IsCanDataFrameGroup())
            .SelectMany(CanDataFrameReader.Read)
            .OrderBy(f => f.Timestamp)
            .ToList();
    }
}

/// <summary>Writes CAN data frames into an ASAM bus logging channel group.</summary>
public sealed class CanDataFrameWriter
{
    private const int DataBytesOffset = 16;

    private readonly Mdf4ChannelGroupWriter _group;
    private readonly byte[] _record;
    private readonly int _maxDataLength;

    internal CanDataFrameWriter(Mdf4Writer writer, CanBusLoggingOptions options)
    {
        _maxDataLength = options.MaxDataLength;
        var source = new Mdf4SourceDefinition(options.NetworkName, Mdf4SourceType.Bus, Mdf4BusType.Can) { Path = options.NetworkName };
        const Mdf4ChannelFlags Bus = Mdf4ChannelFlags.BusEvent;

        Mdf4ChannelDefinition Member(string name, int byteOffset, int bitOffset, int bits, string? unit = null)
            => new(CanBusLogging.FrameChannel + "." + name, Mdf4DataType.UnsignedIntegerLe, bits) { ByteOffset = byteOffset, BitOffset = bitOffset, Flags = Bus, Unit = unit };

        var frame = new Mdf4ChannelDefinition(CanBusLogging.FrameChannel, Mdf4DataType.ByteArray, (DataBytesOffset - 8 + _maxDataLength) * 8)
        {
            ByteOffset = 8,
            Flags = Bus,
            Members = new[]
            {
                Member("BusChannel", 8, 0, 8),
                Member("ID", 9, 0, 29),
                Member("IDE", 12, 7, 1),
                Member("DLC", 13, 0, 4),
                Member("Dir", 13, 4, 1),
                Member("EDL", 13, 6, 1),
                Member("BRS", 13, 7, 1),
                Member("DataLength", 14, 0, 8, "B"),
                Member("ESI", 15, 0, 1),
                new Mdf4ChannelDefinition(CanBusLogging.FrameChannel + ".DataBytes", Mdf4DataType.ByteArray, _maxDataLength * 8) { ByteOffset = DataBytesOffset, Flags = Bus },
            },
        };

        var definition = new Mdf4ChannelGroupDefinition(options.GroupName)
        {
            Source = source,
            PathSeparator = '.',
            Flags = Mdf4ChannelGroupFlags.BusEvent | Mdf4ChannelGroupFlags.PlainBusEvent,
        };
        definition.Add(new Mdf4ChannelDefinition("t", Mdf4DataType.FloatLe, 64)
        {
            ChannelType = Mdf4ChannelType.Master,
            SyncType = Mdf4SyncType.Time,
            Unit = "s",
            ByteOffset = 0,
        });
        definition.Add(frame);

        _group = writer.AddChannelGroup(definition);
        _record = new byte[_group.RecordSize];
    }

    /// <summary>Frames written so far.</summary>
    public long FrameCount => _group.RecordCount;

    /// <summary>The underlying channel group writer.</summary>
    public Mdf4ChannelGroupWriter ChannelGroup => _group;

    /// <summary>Appends a frame.</summary>
    /// <exception cref="ArgumentException">The payload is longer than <see cref="CanBusLoggingOptions.MaxDataLength"/>.</exception>
    public void Write(in CanDataFrame frame)
    {
        if (frame.Data.Length > _maxDataLength)
        {
            throw new ArgumentException($"The group stores at most {_maxDataLength} data bytes; use MaxDataLength = 64 for CAN FD.", nameof(frame));
        }

        Span<byte> record = _record;
        record.Clear();
        Internal.BitField.WriteFloat(record, 0, 64, bigEndian: false, frame.Timestamp);
        record[8] = frame.BusChannel;
        uint id = frame.Id | (frame.IsExtended ? 0x80000000u : 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(9, 4), id);
        record[13] = (byte)(frame.Dlc
            | (frame.Direction == CanDirection.Transmit ? 0x10 : 0)
            | (frame.IsFd ? 0x40 : 0)
            | ((frame.Flags & CanFrameFlags.BitRateSwitch) != 0 ? 0x80 : 0));
        record[14] = (byte)frame.Data.Length;
        record[15] = (byte)((frame.Flags & CanFrameFlags.ErrorStateIndicator) != 0 ? 1 : 0);
        frame.Data.Span.CopyTo(record.Slice(DataBytesOffset));
        _group.WriteRecord(record);
    }

    /// <summary>Appends a frame.</summary>
    public void Write(double timestamp, uint id, ReadOnlySpan<byte> data, CanFrameFlags flags = CanFrameFlags.None, byte busChannel = 1, CanDirection direction = CanDirection.Receive)
        => Write(new CanDataFrame(timestamp, id, data.ToArray(), flags, busChannel, direction));
}

internal static class CanDataFrameReader
{
    public static IEnumerable<CanDataFrame> Read(Mdf4ChannelGroup group)
    {
        Mdf4Channel? Find(string member) => group.FindChannel(CanBusLogging.FrameChannel + "." + member);

        Mdf4Channel id = Find("ID")!;
        Mdf4Channel bytes = Find("DataBytes")!;
        Mdf4Channel? ideChannel = Find("IDE");
        Mdf4Channel? dlcChannel = Find("DLC");
        Mdf4Channel? lengthChannel = Find("DataLength");
        Mdf4Channel? busChannelField = Find("BusChannel");
        Mdf4Channel? dirChannel = Find("Dir");
        Mdf4Channel? edlChannel = Find("EDL");
        Mdf4Channel? brsChannel = Find("BRS");
        Mdf4Channel? esiChannel = Find("ESI");
        Mdf4Channel? master = group.MasterChannel;
        if (master is null && (group.Flags & Mdf4ChannelGroupFlags.RemoteMaster) != 0)
        {
            // Same rule as Mdf4ChannelGroup.ReadTimestamps: record indices are not the time stamps.
            throw new NotSupportedException($"Channel group '{group.Name}' uses the master channel of another group (remote master), which is not supported.");
        }

        master?.EnsureUsableAsMaster();

        // The ASAM bus logging layout: the payload is a stored byte array, the frame fields are
        // stored integers. A group that only has the names right is a format error, not a source
        // of empty frames or of record indices posing as identifiers.
        if (bytes.DataType != Mdf4DataType.ByteArray
            || bytes.ChannelType is not (Mdf4ChannelType.FixedLength or Mdf4ChannelType.MaximumLength or Mdf4ChannelType.VariableLength))
        {
            throw Mdf4FormatException.At(bytes.Offset, $"CAN payload channel '{bytes.Name}' is a {bytes.ChannelType} {bytes.DataType} channel, not stored bytes");
        }

        bytes.ThrowIfUnknownType();
        foreach (Mdf4Channel? field in new[] { id, ideChannel, dlcChannel, lengthChannel, busChannelField, dirChannel, edlChannel, brsChannel, esiChannel })
        {
            field?.ThrowIfUnknownType();
            if (field is not null && (!field.IsInteger || field.ChannelType != Mdf4ChannelType.FixedLength))
            {
                throw Mdf4FormatException.At(field.Offset, $"CAN frame field '{field.Name}' is a {field.ChannelType} {field.DataType} channel, not a stored integer");
            }
        }

        // A field marked invalid in a record carries no value: a frame without a valid identifier,
        // payload, time stamp or frame is not a frame and is skipped; an optional field that is
        // invalid reads as if the group did not have it.
        Mdf4Channel? frame = id.Parent;

        long index = 0;
        foreach (ArraySegment<byte> segment in group.DataGroup.EnumerateRecords(group))
        {
            byte[] record = segment.Array!;
            int count = segment.Count;
            ReadOnlySpan<byte> span = new(record, segment.Offset, count);
            if (id.IsInvalid(span) || bytes.IsInvalid(span) || (master?.IsInvalid(span) ?? false) || (frame?.IsInvalid(span) ?? false))
            {
                index++;
                continue;
            }

            Mdf4Channel? Valid(Mdf4Channel? channel) => channel is not null && !channel.IsInvalid(record.AsSpan(segment.Offset, count)) ? channel : null;
            Mdf4Channel? ide = Valid(ideChannel), dlc = Valid(dlcChannel), length = Valid(lengthChannel), bus = Valid(busChannelField);
            Mdf4Channel? dir = Valid(dirChannel), edl = Valid(edlChannel), brs = Valid(brsChannel), esi = Valid(esiChannel);

            double time = master is null ? index : Physical(master, span, index);
            ulong rawId = (ulong)id.DecodeRawNumber(span, index);
            bool extended = ide is not null ? Flag(ide, span, index) : (rawId & 0x80000000UL) != 0;
            uint frameId = (uint)(rawId & 0x1FFFFFFF);
            bool fd = edl is not null && Flag(edl, span, index);

            byte[] payload = bytes.DecodeRaw(span, index) as byte[]
                ?? throw Mdf4FormatException.At(bytes.Offset, $"CAN payload channel '{bytes.Name}' did not yield bytes");
            // The declared length has to be held by the payload field and be a length a CAN (FD)
            // frame can have; anything else is corruption, not something to clamp into shape.
            double declared = length is not null ? length.DecodeRawNumber(span, index)
                : dlc is not null ? CanDataFrame.DlcToLength((int)dlc.DecodeRawNumber(span, index) & 0x0F, fd)
                : payload.Length;
            if (declared < 0 || declared > payload.Length || CanDataFrame.LengthToDlc((int)declared, fd) < 0)
            {
                throw Mdf4FormatException.At(group.Offset, $"CAN frame {index} declares {declared} data bytes, which its {payload.Length}-byte payload field cannot hold as a {(fd ? "CAN FD" : "CAN")} frame");
            }

            int dataLength = (int)declared;

            CanFrameFlags flags = (extended ? CanFrameFlags.Extended : 0)
                | (fd ? CanFrameFlags.FlexibleDataRate : 0)
                | (fd && brs is not null && Flag(brs, span, index) ? CanFrameFlags.BitRateSwitch : 0)
                | (fd && esi is not null && Flag(esi, span, index) ? CanFrameFlags.ErrorStateIndicator : 0);

            if (!extended && frameId > 0x7FF)
            {
                // A logger that leaves IDE unset for a 29-bit identifier: the identifier decides.
                flags |= CanFrameFlags.Extended;
            }

            var data = new byte[dataLength];
            Buffer.BlockCopy(payload, 0, data, 0, dataLength);
            byte busChannel = bus is null ? (byte)0 : (byte)bus.DecodeRawNumber(span, index);
            CanDirection direction = dir is not null && Flag(dir, span, index) ? CanDirection.Transmit : CanDirection.Receive;

            index++;
            yield return new CanDataFrame(time, frameId, data, flags, busChannel, direction);
        }
    }

    // The field is an integer (checked above); a magnitude test avoids comparing doubles for equality.
    private static bool Flag(Mdf4Channel channel, ReadOnlySpan<byte> record, long index)
        => Math.Abs(channel.DecodeRawNumber(record, index)) > 0;

    private static double Physical(Mdf4Channel channel, ReadOnlySpan<byte> record, long index)
    {
        double raw = channel.DecodeRawNumber(record, index);
        return channel.Conversion is null ? raw : channel.Conversion.ConvertToDouble(raw, channel.IsInteger);
    }
}
