using System;
using System.Collections.Generic;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// Writes the records of one channel group. Obtain it from
/// <see cref="Mdf4Writer.AddChannelGroup(Mdf4ChannelGroupDefinition)"/>.
/// </summary>
/// <remarks>Not thread-safe; a writer and its groups are used from one thread at a time.</remarks>
public sealed class Mdf4ChannelGroupWriter
{
    private readonly Mdf4Writer _writer;
    private readonly Dictionary<Mdf4ChannelDefinition, ChannelLayout> _layouts = new();
    private readonly Mdf4RecordBuilder _builder;

    internal Mdf4ChannelGroupWriter(Mdf4Writer writer, Mdf4ChannelGroupDefinition definition, Mdf4WriterOptions options)
    {
        _writer = writer;
        Definition = definition;
        TopLevel = ChannelLayout.Build(definition, out int recordSize);
        RecordSize = recordSize;
        foreach (ChannelLayout layout in ChannelLayout.Flatten(TopLevel))
        {
            _layouts[layout.Definition] = layout;
            if (layout.Definition.IsVariableLength)
            {
                layout.SignalData = new DataSink(writer, "##SD", options.Compression, options.DataBlockSize, 0);
                HasVariableLengthChannels = true;
            }

            if (layout.Definition.ChannelType == Mdf4ChannelType.Master && Master is null)
            {
                Master = layout.Definition;
            }
        }

        Records = new DataSink(writer, "##DT", options.Compression, options.DataBlockSize, recordSize);
        _builder = new Mdf4RecordBuilder(this);
    }

    /// <summary>The definition this writer was created from.</summary>
    public Mdf4ChannelGroupDefinition Definition { get; }

    /// <summary>Bytes per record.</summary>
    public int RecordSize { get; }

    /// <summary>Records written so far.</summary>
    public long RecordCount { get; private set; }

    /// <summary>The master channel, if the group has one.</summary>
    public Mdf4ChannelDefinition? Master { get; }

    internal IReadOnlyList<ChannelLayout> TopLevel { get; }

    internal DataSink Records { get; }

    internal bool HasVariableLengthChannels { get; }

    /// <summary>
    /// Starts a new record with every value zero (and every variable-length value empty). Set the
    /// values on the returned builder, then call <see cref="Mdf4RecordBuilder.Commit"/>. The
    /// builder is reused: starting a record discards one that was not committed.
    /// </summary>
    public Mdf4RecordBuilder BeginRecord()
    {
        _writer.ThrowIfCompleted();
        _builder.Reset();
        return _builder;
    }

    /// <summary>Starts a new record and sets the master channel (e.g. the time stamp in seconds).</summary>
    /// <exception cref="InvalidOperationException">The group has no master channel.</exception>
    public Mdf4RecordBuilder BeginRecord(double masterValue)
    {
        Mdf4RecordBuilder builder = BeginRecord();
        return builder.Set(Master ?? throw new InvalidOperationException("The channel group has no master channel."), masterValue);
    }

    /// <summary>
    /// Appends a complete record as raw bytes, for callers that encode records themselves. Not
    /// available for groups with variable-length channels.
    /// </summary>
    public void WriteRecord(ReadOnlySpan<byte> record)
    {
        _writer.ThrowIfCompleted();
        if (HasVariableLengthChannels)
        {
            throw new InvalidOperationException("Raw records cannot be written to a group with variable-length channels.");
        }

        if (record.Length != RecordSize)
        {
            throw new ArgumentException($"The record must have {RecordSize} bytes, not {record.Length}.", nameof(record));
        }

        Records.Append(record);
        RecordCount++;
    }

    internal ChannelLayout Layout(Mdf4ChannelDefinition channel)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        return _layouts.TryGetValue(channel, out ChannelLayout? layout)
            ? layout
            : throw new ArgumentException($"Channel '{channel.Name}' does not belong to this channel group.", nameof(channel));
    }

    internal IEnumerable<ChannelLayout> VariableLengthLayouts()
    {
        foreach (ChannelLayout layout in _layouts.Values)
        {
            if (layout.Definition.IsVariableLength)
            {
                yield return layout;
            }
        }
    }

    internal void Commit(byte[] record)
    {
        _writer.ThrowIfCompleted();
        Records.Append(record);
        RecordCount++;
    }
}

/// <summary>Where a channel sits in the record.</summary>
internal sealed class ChannelLayout
{
    private ChannelLayout(Mdf4ChannelDefinition definition, int byteOffset, int bitOffset, int bitCount, IReadOnlyList<ChannelLayout> members)
    {
        Definition = definition;
        ByteOffset = byteOffset;
        BitOffset = bitOffset;
        BitCount = bitCount;
        Members = members;
    }

    public Mdf4ChannelDefinition Definition { get; }

    public int ByteOffset { get; }

    public int BitOffset { get; }

    public int BitCount { get; }

    public IReadOnlyList<ChannelLayout> Members { get; }

    public DataSink? SignalData { get; set; }

    public long SignalDataLink { get; set; }

    public int ByteLength => BitField.ByteSpan(BitOffset, BitCount);

    public int End => ByteOffset + ByteLength;

    public static IReadOnlyList<ChannelLayout> Build(Mdf4ChannelGroupDefinition definition, out int recordSize)
    {
        // Channels with an explicit offset keep it; the rest follow after the last explicit byte,
        // so automatic placement can never overlap an explicit one.
        int cursor = 0;
        foreach (Mdf4ChannelDefinition channel in definition.Channels)
        {
            cursor = Math.Max(cursor, ExplicitEnd(channel));
        }

        var result = new List<ChannelLayout>();
        foreach (Mdf4ChannelDefinition channel in definition.Channels)
        {
            result.Add(Place(channel, ref cursor));
        }

        recordSize = 0;
        foreach (ChannelLayout layout in Flatten(result))
        {
            recordSize = Math.Max(recordSize, layout.End);
        }

        return result;
    }

    public static IEnumerable<ChannelLayout> Flatten(IEnumerable<ChannelLayout> layouts)
    {
        foreach (ChannelLayout layout in layouts)
        {
            yield return layout;
            foreach (ChannelLayout member in Flatten(layout.Members))
            {
                yield return member;
            }
        }
    }

    private static int StoredBits(Mdf4ChannelDefinition channel) => channel.IsVariableLength ? 64 : channel.BitCount;

    private static int ExplicitEnd(Mdf4ChannelDefinition channel)
    {
        int end = channel.ByteOffset is int offset ? offset + BitField.ByteSpan(channel.BitOffset, StoredBits(channel)) : 0;
        foreach (Mdf4ChannelDefinition member in channel.Members)
        {
            end = Math.Max(end, ExplicitEnd(member));
        }

        return end;
    }

    private static ChannelLayout Place(Mdf4ChannelDefinition channel, ref int cursor)
    {
        if (channel.Members.Count == 0)
        {
            int bits = StoredBits(channel);
            if (channel.ByteOffset is int explicitOffset)
            {
                return new ChannelLayout(channel, explicitOffset, channel.BitOffset, bits, Array.Empty<ChannelLayout>());
            }

            var layout = new ChannelLayout(channel, cursor, 0, bits, Array.Empty<ChannelLayout>());
            cursor += layout.ByteLength;
            return layout;
        }

        var members = new List<ChannelLayout>();
        foreach (Mdf4ChannelDefinition member in channel.Members)
        {
            members.Add(Place(member, ref cursor));
        }

        int start = int.MaxValue, end = 0;
        foreach (ChannelLayout member in members)
        {
            start = Math.Min(start, member.ByteOffset);
            end = Math.Max(end, member.End);
        }

        if (channel.ByteOffset is int structureOffset)
        {
            int span = Math.Max(channel.BitCount / 8, end - structureOffset);
            return new ChannelLayout(channel, structureOffset, 0, span * 8, members);
        }

        return new ChannelLayout(channel, start, 0, (end - start) * 8, members);
    }
}
