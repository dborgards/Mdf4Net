using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Mdf4Net.Internal;

/// <summary>Appends a block to the file being written and returns its offset.</summary>
internal interface IBlockAppender
{
    long AppendBlock(string id, long[] links, byte[] data, int offset, int count);

    long AppendBlock(string id, long[] links, byte[] prefix, byte[] data, int offset, int count);
}

/// <summary>
/// A logical byte stream (record data or signal data) written as a sequence of DT/SD blocks, or
/// DZ blocks when compressed. <see cref="Finish"/> links them with DL (and HL) blocks.
/// </summary>
internal sealed class DataSink
{
    private const int EntriesPerList = 1024;

    private readonly IBlockAppender _appender;
    private readonly string _blockId;
    private readonly Mdf4Compression _compression;
    private readonly int _columns;
    private readonly byte[] _buffer;
    private readonly List<long> _blocks = new();
    private readonly List<long> _starts = new();
    private int _used;
    private long _flushed;

    /// <param name="appender">Where blocks go.</param>
    /// <param name="blockId">"##DT" or "##SD".</param>
    /// <param name="compression">Compression of the blocks.</param>
    /// <param name="blockSize">Target uncompressed bytes per block.</param>
    /// <param name="recordSize">
    /// For record data, the record size: blocks then hold whole records, and the transposition
    /// uses it as its column count. Zero for signal data.
    /// </param>
    public DataSink(IBlockAppender appender, string blockId, Mdf4Compression compression, int blockSize, int recordSize)
    {
        _appender = appender;
        _blockId = blockId;
        _compression = compression;
        _columns = recordSize;
        int capacity = recordSize > 0 ? Math.Max(recordSize, blockSize / recordSize * recordSize) : blockSize;
        _buffer = new byte[capacity];
    }

    /// <summary>Total bytes appended so far: the logical offset of the next append.</summary>
    public long Length => _flushed + _used;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_used + data.Length > _buffer.Length)
        {
            FlushBlock();
        }

        if (data.Length > _buffer.Length)
        {
            // An entry larger than a block (a big VLSD value) gets a block of its own.
            byte[] large = data.ToArray();
            WriteBlock(large, large.Length);
            return;
        }

        data.CopyTo(_buffer.AsSpan(_used));
        _used += data.Length;
    }

    public void Append(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        if (_used + first.Length + second.Length > _buffer.Length)
        {
            FlushBlock();
        }

        if (first.Length + second.Length > _buffer.Length)
        {
            var large = new byte[first.Length + second.Length];
            first.CopyTo(large);
            second.CopyTo(large.AsSpan(first.Length));
            WriteBlock(large, large.Length);
            return;
        }

        first.CopyTo(_buffer.AsSpan(_used));
        second.CopyTo(_buffer.AsSpan(_used + first.Length));
        _used += first.Length + second.Length;
    }

    public void FlushBlock()
    {
        if (_used == 0)
        {
            return;
        }

        WriteBlock(_buffer, _used);
        _used = 0;
    }

    /// <summary>Writes what is buffered and returns the link for the stream (0 if empty).</summary>
    public long Finish()
    {
        FlushBlock();
        if (_blocks.Count == 0)
        {
            return 0;
        }

        if (_blocks.Count == 1)
        {
            return _blocks[0];
        }

        // DL blocks are written last to first, so that each knows the offset of its successor.
        long next = 0;
        int lists = (_blocks.Count + EntriesPerList - 1) / EntriesPerList;
        for (int list = lists - 1; list >= 0; list--)
        {
            int first = list * EntriesPerList;
            int count = Math.Min(EntriesPerList, _blocks.Count - first);
            var links = new long[count + 1];
            links[0] = next;
            var data = new byte[8 + (count * 8)];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), (uint)count);
            for (int i = 0; i < count; i++)
            {
                links[i + 1] = _blocks[first + i];
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8 + (i * 8), 8), _starts[first + i]);
            }

            next = _appender.AppendBlock("##DL", links, data, 0, data.Length);
        }

        if (_compression == Mdf4Compression.None)
        {
            return next;
        }

        // MDF 4.1: a list of DZ blocks is announced by an HL block in front of the first DL.
        var hl = new byte[8];
        hl[2] = (byte)(UsesTransposition ? 1 : 0);
        return _appender.AppendBlock("##HL", new[] { next }, hl, 0, hl.Length);
    }

    private bool UsesTransposition => _compression == Mdf4Compression.TransposedDeflate && _columns > 1;

    private void WriteBlock(byte[] data, int count)
    {
        _starts.Add(_flushed);
        _flushed += count;

        if (_compression == Mdf4Compression.None)
        {
            _blocks.Add(_appender.AppendBlock(_blockId, Array.Empty<long>(), data, 0, count));
            return;
        }

        byte[] source = data;
        int zipParameter = 0;
        byte zipType = 0;
        if (UsesTransposition)
        {
            source = Transposition.Transpose(data, 0, count, _columns);
            zipParameter = _columns;
            zipType = 1;
        }

        byte[] compressed = Zlib.Compress(source, 0, count);
        var fields = new byte[24];
        Encoding.ASCII.GetBytes(_blockId, 2, 2, fields, 0);
        fields[2] = zipType;
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(4, 4), (uint)zipParameter);
        BinaryPrimitives.WriteUInt64LittleEndian(fields.AsSpan(8, 8), (ulong)count);
        BinaryPrimitives.WriteUInt64LittleEndian(fields.AsSpan(16, 8), (ulong)compressed.Length);
        _blocks.Add(_appender.AppendBlock("##DZ", Array.Empty<long>(), fields, compressed, 0, compressed.Length));
    }
}
