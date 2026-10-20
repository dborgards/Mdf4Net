using System;
using System.Collections.Generic;
using System.Linq;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// A channel group (CG): a set of channels sampled together, stored as fixed-length records.
/// </summary>
public sealed class Mdf4ChannelGroup
{
    private readonly List<Mdf4Channel> _channels = new();
    private readonly List<Mdf4Channel> _allChannels = new();
    private readonly Dictionary<long, Mdf4Channel> _byOffset = new();

    internal Mdf4ChannelGroup(Mdf4DataGroup dataGroup, RawBlock block, int index, HashSet<long> visited)
    {
        DataGroup = dataGroup;
        Index = index;
        Offset = block.Offset;
        BlockReader reader = dataGroup.File.Reader;

        RecordId = block.U64(0);
        CycleCount = block.U64(8);
        Flags = (Mdf4ChannelGroupFlags)block.U16(16);
        ushort separator = block.U16(18);
        PathSeparator = separator == 0 ? null : (char)separator;
        DataBytes = block.U32(24);
        InvalidationBytes = block.U32(28);

        Name = TextBlocks.ReadText(reader, block.Link(2));
        Source = Mdf4SourceInformation.Read(reader, block.Link(3));
        Comment = TextBlocks.ReadText(reader, block.Link(5));

        if (IsVariableLengthSignalData)
        {
            // A VLSD group has no channels; its two size fields together hold the data length.
            VariableLengthDataSize = ((ulong)InvalidationBytes << 32) | DataBytes;
            return;
        }

        if ((long)DataBytes + InvalidationBytes > reader.Options.MaxRecordSize)
        {
            throw Mdf4FormatException.At(block.Offset, $"CG record size {DataBytes}+{InvalidationBytes} exceeds MaxRecordSize");
        }

        for (long cn = block.Link(1); cn != 0;)
        {
            BlockReader.Visit(visited, cn, "CN");
            RawBlock cnBlock = reader.ReadBlock(cn, "##CN");
            var channel = new Mdf4Channel(this, null, cnBlock, visited, depth: 0);
            _channels.Add(channel);
            cn = cnBlock.Link(0);
        }

        foreach (Mdf4Channel channel in _channels)
        {
            Collect(channel);
        }

        MasterChannel = _allChannels.FirstOrDefault(c => c.ChannelType is Mdf4ChannelType.Master or Mdf4ChannelType.VirtualMaster);
    }

    /// <summary>The data group this channel group belongs to.</summary>
    public Mdf4DataGroup DataGroup { get; }

    /// <summary>Zero-based position within the data group.</summary>
    public int Index { get; }

    /// <summary>File offset of the CG block.</summary>
    public long Offset { get; }

    /// <summary>The acquisition name of the group, if any.</summary>
    public string? Name { get; }

    /// <summary>The group comment.</summary>
    public string? Comment { get; }

    /// <summary>The acquisition source, if any.</summary>
    public Mdf4SourceInformation? Source { get; }

    /// <summary>The record id that marks this group's records in an unsorted data group.</summary>
    public ulong RecordId { get; }

    /// <summary>
    /// The number of records the writer declared. For an unfinalized file this may be stale; the
    /// read methods do not rely on it there.
    /// </summary>
    public ulong CycleCount { get; }

    /// <summary>The group flags.</summary>
    public Mdf4ChannelGroupFlags Flags { get; }

    /// <summary>The separator used in channel names of structures, e.g. <c>.</c>, if declared.</summary>
    public char? PathSeparator { get; }

    /// <summary>Bytes of a record holding channel values.</summary>
    public uint DataBytes { get; }

    /// <summary>Bytes of invalidation bits at the end of each record.</summary>
    public uint InvalidationBytes { get; }

    /// <summary>True if this group only holds the variable-length data of a VLSD channel.</summary>
    public bool IsVariableLengthSignalData => (Flags & Mdf4ChannelGroupFlags.VariableLengthSignalData) != 0;

    /// <summary>True if the group holds ASAM bus logging events.</summary>
    public bool IsBusEvent => (Flags & Mdf4ChannelGroupFlags.BusEvent) != 0;

    /// <summary>The top-level channels, in file order.</summary>
    public IReadOnlyList<Mdf4Channel> Channels => _channels;

    /// <summary>All channels including composition members, depth first.</summary>
    public IReadOnlyList<Mdf4Channel> AllChannels => _allChannels;

    /// <summary>The master channel (time, angle, distance or index), if the group has one.</summary>
    public Mdf4Channel? MasterChannel { get; }

    internal ulong VariableLengthDataSize { get; }

    internal int RecordSize => IsVariableLengthSignalData ? 0 : (int)(DataBytes + InvalidationBytes);

    /// <summary>The first channel (including composition members) with this name.</summary>
    public Mdf4Channel? FindChannel(string name) => _allChannels.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// The master channel's values, converted to physical units (normally seconds). For a group
    /// without a master channel, the zero-based record index.
    /// </summary>
    public double[] ReadTimestamps()
    {
        if (MasterChannel is not null)
        {
            if (!MasterChannel.IsNumeric)
            {
                throw Mdf4FormatException.At(MasterChannel.Offset, $"Master channel '{MasterChannel.Name}' holds {MasterChannel.DataType} values");
            }

            return MasterChannel.ReadDoubles();
        }

        long count = CountRecords();
        var result = new double[count];
        for (long i = 0; i < count; i++)
        {
            result[i] = i;
        }

        return result;
    }

    /// <summary>Counts the records of this group by reading through the data.</summary>
    public long CountRecords()
    {
        long count = 0;
        using IEnumerator<ArraySegment<byte>> records = DataGroup.EnumerateRecords(this).GetEnumerator();
        while (records.MoveNext())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Yields a copy of every record's bytes (channel values followed by invalidation bytes),
    /// for decoding layouts this library does not interpret itself.
    /// </summary>
    public IEnumerable<byte[]> ReadRecordBytes()
    {
        foreach (ArraySegment<byte> record in DataGroup.EnumerateRecords(this))
        {
            var copy = new byte[record.Count];
            Buffer.BlockCopy(record.Array!, record.Offset, copy, 0, record.Count);
            yield return copy;
        }
    }

    internal Mdf4Channel? ChannelAt(long offset) => _byOffset.TryGetValue(offset, out Mdf4Channel? channel) ? channel : null;

    internal void ResolveLinks()
    {
        foreach (Mdf4Channel channel in _allChannels)
        {
            channel.ResolveDataLink();
        }
    }

    private void Collect(Mdf4Channel channel)
    {
        _allChannels.Add(channel);
        _byOffset[channel.Offset] = channel;
        foreach (Mdf4Channel child in channel.Children)
        {
            Collect(child);
        }
    }
}
