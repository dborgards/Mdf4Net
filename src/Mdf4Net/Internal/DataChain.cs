using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Mdf4Net.Internal;

/// <summary>
/// Turns a data link (DT, SD, DZ, DL or HL) into the logical byte stream it describes, one
/// chunk at a time, so that a recording larger than memory can be read.
/// </summary>
internal static class DataChain
{
    private const int SliceSize = 1024 * 1024;

    public static IEnumerable<ArraySegment<byte>> Enumerate(BlockReader reader, long link, string leafId)
    {
        if (link == 0)
        {
            yield break;
        }

        var visited = new HashSet<long>();
        BlockHeader first = reader.ReadHeader(link);
        if (first.Id == "##HL")
        {
            BlockReader.Visit(visited, link, "HL");
            RawBlock hl = reader.ReadBlock(link, "##HL");
            link = hl.Link(0);
            if (link == 0)
            {
                yield break;
            }

            first = reader.ReadHeader(link);
        }

        if (first.Id != "##DL")
        {
            foreach (ArraySegment<byte> chunk in Leaf(reader, first, leafId, visited))
            {
                yield return chunk;
            }

            yield break;
        }

        long dl = link;
        while (dl != 0)
        {
            BlockReader.Visit(visited, dl, "DL");
            RawBlock list = reader.ReadBlock(dl, "##DL");
            uint count = list.U32(4);
            if (count > list.Links.Length - 1)
            {
                throw Mdf4FormatException.At(dl, $"DL block lists {count} data blocks but has {list.Links.Length - 1} links");
            }

            for (int i = 1; i <= count; i++)
            {
                long entry = list.Links[i];
                if (entry == 0)
                {
                    continue;
                }

                BlockHeader header = reader.ReadHeader(entry);
                foreach (ArraySegment<byte> chunk in Leaf(reader, header, leafId, visited))
                {
                    yield return chunk;
                }
            }

            dl = list.Link(0);
        }
    }

    private static IEnumerable<ArraySegment<byte>> Leaf(BlockReader reader, BlockHeader header, string leafId, HashSet<long> visited)
    {
        BlockReader.Visit(visited, header.Offset, header.Id);
        if (header.Id == leafId)
        {
            long position = header.DataOffset;
            long remaining = header.DataLength;
            while (remaining > 0)
            {
                int size = (int)Math.Min(SliceSize, remaining);
                yield return new ArraySegment<byte>(reader.ReadBytes(position, size));
                position += size;
                remaining -= size;
            }

            yield break;
        }

        if (header.Id == "##DZ")
        {
            yield return new ArraySegment<byte>(Inflate(reader, header, leafId));
            yield break;
        }

        throw Mdf4FormatException.At(header.Offset, $"Expected {leafId}, DZ or DL data, found {header.Id} (not supported)");
    }

    private static byte[] Inflate(BlockReader reader, BlockHeader header, string leafId)
    {
        const int FieldsSize = 24;
        if (header.LinkCount != 0 || header.DataLength < FieldsSize)
        {
            throw Mdf4FormatException.At(header.Offset, "DZ block is malformed");
        }

        byte[] fields = reader.ReadBytes(header.DataOffset, FieldsSize);
        string original = "##" + Encoding.ASCII.GetString(fields, 0, 2);
        byte zipType = fields[2];
        uint zipParameter = BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(4, 4));
        ulong originalLength = BinaryPrimitives.ReadUInt64LittleEndian(fields.AsSpan(8, 8));
        ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(fields.AsSpan(16, 8));

        if (original != leafId)
        {
            throw Mdf4FormatException.At(header.Offset, $"DZ block compresses a {original} block where {leafId} was expected");
        }

        if (originalLength > (ulong)reader.Options.MaxDecompressedBlockSize)
        {
            throw Mdf4FormatException.At(header.Offset, $"DZ block declares {originalLength} bytes, more than MaxDecompressedBlockSize");
        }

        ulong maxCompressed = (ulong)reader.Options.MaxDecompressedBlockSize + ((ulong)reader.Options.MaxDecompressedBlockSize / 8) + 1024;
        if (dataLength > (ulong)(header.DataLength - FieldsSize) || dataLength > maxCompressed || dataLength > int.MaxValue)
        {
            throw Mdf4FormatException.At(header.Offset, "DZ block data length exceeds the block");
        }

        byte[] compressed = reader.ReadBytes(header.DataOffset + FieldsSize, (int)dataLength);
        byte[] inflated = Zlib.Decompress(compressed, (int)originalLength, header.Offset);
        return zipType switch
        {
            0 => inflated,
            1 => Transposition.Untranspose(inflated, zipParameter > int.MaxValue ? 0 : (int)zipParameter),
            _ => throw Mdf4FormatException.At(header.Offset, $"DZ zip type {zipType} is not supported"),
        };
    }
}

/// <summary>Sequential reads across the chunks of a <see cref="DataChain"/>.</summary>
internal sealed class ChunkStream : IDisposable
{
    private readonly IEnumerator<ArraySegment<byte>> _chunks;
    private ArraySegment<byte> _current;
    private int _position;
    private bool _ended;

    public ChunkStream(IEnumerable<ArraySegment<byte>> chunks)
    {
        _chunks = chunks.GetEnumerator();
    }

    /// <summary>True once a read hit the end in the middle of a record (an unfinished file).</summary>
    public bool Truncated { get; private set; }

    public long BytesRead { get; private set; }

    /// <summary>Fills the buffer completely, or returns false at the end of the data.</summary>
    public bool TryRead(byte[] buffer, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            if (!EnsureData())
            {
                if (done > 0)
                {
                    Truncated = true;
                }

                return false;
            }

            int take = Math.Min(count - done, _current.Count - _position);
            Buffer.BlockCopy(_current.Array!, _current.Offset + _position, buffer, offset + done, take);
            _position += take;
            done += take;
            BytesRead += take;
        }

        return true;
    }

    public bool TrySkip(long count)
    {
        long done = 0;
        while (done < count)
        {
            if (!EnsureData())
            {
                if (done > 0)
                {
                    Truncated = true;
                }

                return false;
            }

            int take = (int)Math.Min(count - done, _current.Count - _position);
            _position += take;
            done += take;
            BytesRead += take;
        }

        return true;
    }

    private bool EnsureData()
    {
        while (_current.Array is null || _position >= _current.Count)
        {
            if (_ended || !_chunks.MoveNext())
            {
                _ended = true;
                return false;
            }

            _current = _chunks.Current;
            _position = 0;
        }

        return true;
    }

    public void Dispose() => _chunks.Dispose();
}
