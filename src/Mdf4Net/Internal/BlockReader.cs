using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mdf4Net.Internal;

internal readonly struct BlockHeader
{
    public BlockHeader(long offset, string id, long length, long linkCount)
    {
        Offset = offset;
        Id = id;
        Length = length;
        LinkCount = linkCount;
    }

    public long Offset { get; }

    public string Id { get; }

    public long Length { get; }

    public long LinkCount { get; }

    public long DataOffset => Offset + 24 + (LinkCount * 8);

    public long DataLength => Length - 24 - (LinkCount * 8);
}

/// <summary>A metadata block read completely into memory.</summary>
internal sealed class RawBlock
{
    public RawBlock(BlockHeader header, long[] links, byte[] data)
    {
        Header = header;
        Links = links;
        Data = data;
    }

    public BlockHeader Header { get; }

    public long Offset => Header.Offset;

    public string Id => Header.Id;

    public long[] Links { get; }

    public byte[] Data { get; }

    public long Link(int index) => index < Links.Length ? Links[index] : 0;

    public ReadOnlySpan<byte> DataAt(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Data.Length)
        {
            throw Mdf4FormatException.At(Offset, $"{Id} block is too short: needs {offset + length} data bytes, has {Data.Length}");
        }

        return Data.AsSpan(offset, length);
    }

    public byte U8(int offset) => DataAt(offset, 1)[0];

    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(DataAt(offset, 2));

    public short I16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(DataAt(offset, 2));

    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(DataAt(offset, 4));

    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(DataAt(offset, 8));

    public double F64(int offset) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(DataAt(offset, 8)));

    public bool HasData(int offset, int length) => offset + length <= Data.Length;
}

/// <summary>
/// Reads blocks from the underlying stream, validating every header against the file before
/// trusting a length or a count from it.
/// </summary>
internal sealed class BlockReader
{
    public const int HeaderSize = 24;
    public const int IdentificationSize = 64;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[HeaderSize];

    public BlockReader(Stream stream, Mdf4ReaderOptions options)
    {
        _stream = stream;
        Options = options;
        FileLength = stream.Length;
    }

    public Mdf4ReaderOptions Options { get; }

    public long FileLength { get; }

    public BlockHeader ReadHeader(long offset)
    {
        if (offset < IdentificationSize || offset > FileLength - HeaderSize)
        {
            throw Mdf4FormatException.At(offset, "Link points outside the file");
        }

        ReadExactly(offset, _header, 0, HeaderSize);
        if (_header[0] != (byte)'#' || _header[1] != (byte)'#')
        {
            throw Mdf4FormatException.At(offset, "Link does not point at a block");
        }

        string id = Encoding.ASCII.GetString(_header, 0, 4);
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(8, 8));
        ulong linkCount = BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(16, 8));

        if (length < HeaderSize || length > (ulong)(FileLength - offset))
        {
            throw Mdf4FormatException.At(offset, $"{id} block length {length} does not fit the file");
        }

        if (linkCount > (length - HeaderSize) / 8)
        {
            throw Mdf4FormatException.At(offset, $"{id} block declares {linkCount} links, more than its length holds");
        }

        return new BlockHeader(offset, id, (long)length, (long)linkCount);
    }

    public RawBlock ReadBlock(long offset, string expectedId)
    {
        RawBlock block = ReadBlock(offset);
        if (block.Id != expectedId)
        {
            throw Mdf4FormatException.At(offset, $"Expected a {expectedId} block, found {block.Id}");
        }

        return block;
    }

    public RawBlock ReadBlock(long offset)
    {
        BlockHeader header = ReadHeader(offset);
        if (header.Length > Options.MaxMetadataBlockSize)
        {
            throw Mdf4FormatException.At(offset, $"{header.Id} block of {header.Length} bytes exceeds MaxMetadataBlockSize");
        }

        int linkBytes = checked((int)(header.LinkCount * 8));
        int dataBytes = checked((int)header.DataLength);
        var buffer = new byte[linkBytes + dataBytes];
        ReadExactly(offset + HeaderSize, buffer, 0, buffer.Length);

        var links = new long[header.LinkCount];
        for (int i = 0; i < links.Length; i++)
        {
            long link = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * 8, 8));
            if (link != 0 && (link < IdentificationSize || link > FileLength - HeaderSize))
            {
                throw Mdf4FormatException.At(offset, $"{header.Id} block link {i} points outside the file");
            }

            links[i] = link;
        }

        var data = new byte[dataBytes];
        Buffer.BlockCopy(buffer, linkBytes, data, 0, dataBytes);
        return new RawBlock(header, links, data);
    }

    public void ReadExactly(long offset, byte[] buffer, int index, int count)
    {
        if (offset < 0 || count < 0 || offset > FileLength - count)
        {
            throw Mdf4FormatException.At(offset, "Read past the end of the file");
        }

        _stream.Position = offset;
        int total = 0;
        while (total < count)
        {
            int read = _stream.Read(buffer, index + total, count - total);
            if (read == 0)
            {
                throw Mdf4FormatException.At(offset, "Unexpected end of file");
            }

            total += read;
        }
    }

    public byte[] ReadBytes(long offset, int count)
    {
        var buffer = new byte[count];
        ReadExactly(offset, buffer, 0, count);
        return buffer;
    }

    /// <summary>Guards linked lists against cycles: every block may be visited once.</summary>
    public static void Visit(HashSet<long> visited, long offset, string what)
    {
        if (!visited.Add(offset))
        {
            throw Mdf4FormatException.At(offset, $"The {what} chain loops back on itself");
        }
    }
}
