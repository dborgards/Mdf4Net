using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// A data group (DG): one block of record data shared by one channel group (sorted) or several
/// channel groups whose records are told apart by a record id (unsorted).
/// </summary>
public sealed class Mdf4DataGroup
{
    private readonly List<Mdf4ChannelGroup> _channelGroups = new();
    private readonly Dictionary<ulong, Mdf4ChannelGroup> _byRecordId = new();

    internal Mdf4DataGroup(Mdf4File file, RawBlock block, int index, HashSet<long> visited)
    {
        File = file;
        Index = index;
        Offset = block.Offset;
        DataLink = block.Link(2);
        RecordIdSize = block.U8(0);
        if (RecordIdSize is not (0 or 1 or 2 or 4 or 8))
        {
            throw Mdf4FormatException.At(block.Offset, $"DG record id size {RecordIdSize} is not 0, 1, 2, 4 or 8");
        }

        Comment = TextBlocks.ReadText(file.Reader, block.Link(3));

        int cgIndex = 0;
        for (long cg = block.Link(1); cg != 0;)
        {
            BlockReader.Visit(visited, cg, "CG");
            RawBlock cgBlock = file.Reader.ReadBlock(cg, "##CG");
            var group = new Mdf4ChannelGroup(this, cgBlock, cgIndex++, visited);
            _channelGroups.Add(group);
            if (RecordIdSize > 0)
            {
                if (_byRecordId.ContainsKey(group.RecordId))
                {
                    throw Mdf4FormatException.At(cg, $"Record id {group.RecordId} is used twice in one data group");
                }

                _byRecordId[group.RecordId] = group;
            }

            cg = cgBlock.Link(0);
        }

        if (RecordIdSize == 0 && _channelGroups.Count > 1)
        {
            throw Mdf4FormatException.At(block.Offset, "A sorted data group (record id size 0) holds more than one channel group");
        }

        foreach (Mdf4ChannelGroup group in _channelGroups)
        {
            group.ResolveLinks();
        }
    }

    /// <summary>The file this group belongs to.</summary>
    public Mdf4File File { get; }

    /// <summary>Zero-based position of the group in the file.</summary>
    public int Index { get; }

    /// <summary>File offset of the DG block.</summary>
    public long Offset { get; }

    /// <summary>
    /// Size of the record id in front of each record, in bytes. Zero for a sorted group, which
    /// holds exactly one channel group.
    /// </summary>
    public int RecordIdSize { get; }

    /// <summary>True if the records of several channel groups are interleaved (record id size &gt; 0).</summary>
    public bool IsUnsorted => RecordIdSize > 0;

    /// <summary>The data group comment.</summary>
    public string? Comment { get; }

    /// <summary>The channel groups of this data group.</summary>
    public IReadOnlyList<Mdf4ChannelGroup> ChannelGroups => _channelGroups;

    internal long DataLink { get; }

    internal Mdf4ChannelGroup? FindByOffset(long offset)
    {
        foreach (Mdf4ChannelGroup group in _channelGroups)
        {
            if (group.Offset == offset)
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a VLSD record (4-byte length plus data) into the buffer, growing it in steps of at
    /// most 1 MiB as data actually arrives, so a forged length cannot force a large allocation.
    /// </summary>
    private static bool ReadGrowing(ChunkStream data, ref byte[] buffer, byte[] lengthBytes, int length)
    {
        const int Step = 1024 * 1024;
        int total = length + 4;
        if (buffer.Length < 4)
        {
            buffer = new byte[Math.Min(total, Step)];
        }

        Buffer.BlockCopy(lengthBytes, 0, buffer, 0, 4);
        int done = 4;
        while (done < total)
        {
            if (buffer.Length == done)
            {
                Array.Resize(ref buffer, (int)Math.Min(total, (long)buffer.Length + Step));
            }

            int take = Math.Min(total, buffer.Length) - done;
            if (!data.TryRead(buffer, done, take))
            {
                return false;
            }

            done += take;
        }

        return true;
    }

    /// <summary>
    /// Yields the records of one channel group. The yielded segment is a reused buffer that is
    /// only valid until the next iteration. For a VLSD group a record is the 4-byte length plus
    /// the data bytes, exactly as in an SD block.
    /// </summary>
    internal IEnumerable<ArraySegment<byte>> EnumerateRecords(Mdf4ChannelGroup target)
    {
        File.ThrowIfDisposed();
        using var data = new ChunkStream(DataChain.Enumerate(File.Reader, DataLink, "##DT"));
        long limit = File.IsFinalized && !target.IsVariableLengthSignalData ? (long)Math.Min(target.CycleCount, long.MaxValue) : long.MaxValue;
        long yielded = 0;

        // The data ended. For an unfinalized file that is how a recording stops; for a finalized
        // one, a record cut off or fewer records than the group declares mean the file is broken,
        // and returning what was read would pass off an incomplete measurement as complete.
        void EndOfData(bool atRecordStart)
        {
            if (!File.IsFinalized)
            {
                return;
            }

            if (data.Truncated || !atRecordStart)
            {
                throw Mdf4FormatException.At(Offset, "The data of a finalized file ends in the middle of a record");
            }

            if (limit != long.MaxValue && yielded < limit)
            {
                throw Mdf4FormatException.At(target.Offset, $"Channel group declares {limit} records, the data holds {yielded}");
            }
        }

        var id = new byte[8];
        byte[] buffer = new byte[Math.Max(target.RecordSize, 4)];

        if (RecordIdSize == 0 && target.RecordSize == 0 && !target.IsVariableLengthSignalData)
        {
            // Only virtual channels: no record bytes are stored, and the cycle count of a
            // finalized group says how many records there are. An unfinalized file has no
            // reliable count, and no data to count either.
            if (!File.IsFinalized)
            {
                yield break;
            }

            if (target.CycleCount > (ulong)File.Reader.Options.MaxVirtualRecordCount)
            {
                throw Mdf4FormatException.At(target.Offset, $"Channel group declares {target.CycleCount} records without data, more than MaxVirtualRecordCount");
            }

            for (ulong i = 0; i < target.CycleCount; i++)
            {
                yield return new ArraySegment<byte>(buffer, 0, 0);
            }

            yield break;
        }

        while (yielded < limit)
        {
            Mdf4ChannelGroup group = target;
            if (RecordIdSize > 0)
            {
                if (!data.TryRead(id, 0, RecordIdSize))
                {
                    EndOfData(atRecordStart: true);
                    yield break;
                }

                ulong recordId = RecordIdSize switch
                {
                    1 => id[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(id),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(id),
                    _ => BinaryPrimitives.ReadUInt64LittleEndian(id),
                };
                if (!_byRecordId.TryGetValue(recordId, out Mdf4ChannelGroup? found))
                {
                    throw Mdf4FormatException.At(Offset, $"Data group holds a record with unknown record id {recordId}");
                }

                group = found;
            }

            if (group.IsVariableLengthSignalData)
            {
                if (!data.TryRead(id, 0, 4))
                {
                    EndOfData(atRecordStart: RecordIdSize == 0);
                    yield break;
                }

                uint length = BinaryPrimitives.ReadUInt32LittleEndian(id);
                if (!ReferenceEquals(group, target))
                {
                    if (!data.TrySkip(length))
                    {
                        EndOfData(atRecordStart: false);
                        yield break;
                    }

                    continue;
                }

                if (length > File.Reader.Options.MaxSignalDataSize || length > int.MaxValue - 4)
                {
                    throw Mdf4FormatException.At(group.Offset, $"VLSD record of {length} bytes exceeds MaxSignalDataSize");
                }

                if (!ReadGrowing(data, ref buffer, id, (int)length))
                {
                    EndOfData(atRecordStart: false);
                    yield break;
                }

                yielded++;
                yield return new ArraySegment<byte>(buffer, 0, (int)length + 4);
                continue;
            }

            int size = group.RecordSize;
            if (size == 0)
            {
                // Unsorted: each record is its record id alone (sorted groups are handled above).
                if (ReferenceEquals(group, target))
                {
                    yielded++;
                    yield return new ArraySegment<byte>(buffer, 0, 0);
                }

                continue;
            }

            if (!ReferenceEquals(group, target))
            {
                if (!data.TrySkip(size))
                {
                    EndOfData(atRecordStart: RecordIdSize == 0);
                    yield break;
                }

                continue;
            }

            if (!data.TryRead(buffer, 0, size))
            {
                EndOfData(atRecordStart: RecordIdSize == 0);
                yield break;
            }

            yielded++;
            yield return new ArraySegment<byte>(buffer, 0, size);
        }
    }
}
