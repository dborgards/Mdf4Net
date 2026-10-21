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
    private readonly long _masterGroupLink;

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
        _masterGroupLink = block.Link(6);

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

        List<Mdf4Channel> masters = _allChannels.Where(c => c.ChannelType is Mdf4ChannelType.Master or Mdf4ChannelType.VirtualMaster).ToList();
        if (masters.Count > 1)
        {
            throw Mdf4FormatException.At(block.Offset, $"Channel group has {masters.Count} master channels; which one applies would depend on their order");
        }

        MasterChannel = masters.FirstOrDefault();
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

    /// <summary>The group's own master channel (time, angle, distance or index), if it has one.</summary>
    public Mdf4Channel? MasterChannel { get; }

    /// <summary>
    /// The channel group whose master channel this group uses, if the group takes its master
    /// from another group (<see cref="Mdf4ChannelGroupFlags.RemoteMaster"/>, MDF 4.20). A chain
    /// of such references is followed to the group that holds the master channel, which may be
    /// in another data group. Null if the group has its own master channel or none.
    /// </summary>
    public Mdf4ChannelGroup? RemoteMasterGroup { get; private set; }

    /// <summary>
    /// The master channel that applies to this group's records: its own, or that of
    /// <see cref="RemoteMasterGroup"/>. Null if the group has neither.
    /// </summary>
    public Mdf4Channel? ResolvedMasterChannel => MasterChannel ?? RemoteMasterGroup?.MasterChannel;

    internal ulong VariableLengthDataSize { get; }

    internal int RecordSize => IsVariableLengthSignalData ? 0 : (int)(DataBytes + InvalidationBytes);

    /// <summary>The first channel (including composition members) with this name.</summary>
    public Mdf4Channel? FindChannel(string name) => _allChannels.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// The master channel's values, converted to physical units (normally seconds), one per
    /// record of this group. For a group with a <see cref="RemoteMasterGroup"/> these are that
    /// group's values, which must be as many as this group has records. For a group without any
    /// master channel, the zero-based record index.
    /// </summary>
    public double[] ReadTimestamps() => ReadTimestamps(invalid: null);

    /// <summary>
    /// <see cref="ReadTimestamps()"/>, also recording per record whether the master sample is
    /// invalid (never for record indices).
    /// </summary>
    internal double[] ReadTimestamps(List<bool>? invalid)
    {
        if (MasterChannel is not null)
        {
            MasterChannel.EnsureUsableAsMaster();
            return MasterChannel.ReadDoubles(invalid);
        }

        long count = CountRecords();
        if (RemoteMasterGroup is not null)
        {
            double[] remote = RemoteMasterGroup.ReadTimestamps(invalid);
            if (remote.Length != count)
            {
                throw RecordCountMismatch(count, remote.Length);
            }

            return remote;
        }

        invalid?.AddRange(Enumerable.Repeat(false, (int)count));
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

    /// <summary>
    /// The i-th record takes the i-th value of the remote master; a group with more or fewer
    /// records than its master group has values is inconsistent, and neither cutting the records
    /// short nor padding the time stamps makes it consistent.
    /// </summary>
    internal Mdf4FormatException RecordCountMismatch(long records, int masterValues)
        => Mdf4FormatException.At(Offset, $"Channel group has {records} records, its remote master group has {masterValues} master values");

    internal Mdf4Channel? ChannelAt(long offset) => _byOffset.TryGetValue(offset, out Mdf4Channel? channel) ? channel : null;

    internal void ResolveLinks()
    {
        foreach (Mdf4Channel channel in _allChannels)
        {
            channel.ResolveDataLink();
        }
    }

    /// <summary>
    /// Follows <c>cg_cg_master</c> to the group that holds the master channel. Runs once every
    /// data group is loaded, since the master group may come later in the file.
    /// </summary>
    internal void ResolveRemoteMaster()
    {
        if ((Flags & Mdf4ChannelGroupFlags.RemoteMaster) == 0)
        {
            return;
        }

        if (MasterChannel is not null)
        {
            throw Mdf4FormatException.At(Offset, "Channel group has a master channel of its own and refers to a remote master group; which one applies is ambiguous");
        }

        // Every group on the chain is visited at most once, so a cycle is found and the walk ends.
        var visited = new HashSet<long> { Offset };
        Mdf4ChannelGroup group = this;
        while (true)
        {
            if (group._masterGroupLink == 0)
            {
                throw Mdf4FormatException.At(group.Offset, "Channel group is flagged as using a remote master but has no cg_cg_master link");
            }

            if (!visited.Add(group._masterGroupLink))
            {
                throw Mdf4FormatException.At(Offset, "The remote master chain of the channel group loops back on itself");
            }

            Mdf4ChannelGroup target = DataGroup.File.FindChannelGroup(group._masterGroupLink)
                ?? throw Mdf4FormatException.At(group.Offset, $"cg_cg_master link 0x{group._masterGroupLink:X} does not refer to a channel group of the file");
            if (target.IsVariableLengthSignalData)
            {
                throw Mdf4FormatException.At(group.Offset, "Channel group refers to a VLSD channel group as its remote master group");
            }

            if ((target.Flags & Mdf4ChannelGroupFlags.RemoteMaster) != 0)
            {
                group = target;
                continue;
            }

            if (target.MasterChannel is null)
            {
                throw Mdf4FormatException.At(group.Offset, "Channel group refers to a remote master group that has no master channel");
            }

            RemoteMasterGroup = target;
            return;
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
