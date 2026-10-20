using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Mdf4Net.Internal;

namespace Mdf4Net.Tests.Infrastructure;

/// <summary>
/// Builds MDF files block by block, for layouts the writer never produces (unsorted data groups,
/// VLSD channel groups, invalidation bits, virtual and maximum-length channels) and for files
/// that are deliberately broken.
/// </summary>
internal sealed class RawMdf
{
    private readonly MetadataBuilder _meta = new();

    public RawMdf()
    {
        // The HD block has to be the first block, at offset 64.
        Header = _meta.Add("##HD", new object?[6], new byte[32]);
    }

    public PendingBlock Header { get; }

    public PendingBlock Text(string text) => _meta.Text(text)!;

    public PendingBlock Block(string id, object?[] links, byte[] data) => _meta.Add(id, links, data);

    public PendingBlock DataGroup(byte recordIdSize, PendingBlock firstChannelGroup, PendingBlock? data, PendingBlock? next = null)
    {
        var bytes = new byte[8];
        bytes[0] = recordIdSize;
        return _meta.Add("##DG", new object?[] { next, firstChannelGroup, data, null }, bytes);
    }

    public PendingBlock ChannelGroup(ulong recordId, ulong cycles, uint dataBytes, uint invalidationBytes, PendingBlock? firstChannel, string? name = null, ushort flags = 0, PendingBlock? next = null)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0), recordId);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), cycles);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), dataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), invalidationBytes);
        return _meta.Add("##CG", new object?[] { next, firstChannel, name is null ? null : Text(name), null, null, null }, data);
    }

    public PendingBlock Channel(
        string name,
        Mdf4ChannelType type,
        Mdf4DataType dataType,
        uint byteOffset,
        uint bitCount,
        PendingBlock? next = null,
        Mdf4SyncType sync = Mdf4SyncType.None,
        byte bitOffset = 0,
        Mdf4ChannelFlags flags = Mdf4ChannelFlags.None,
        uint invalidationBit = 0,
        PendingBlock? conversion = null,
        object? data = null,
        PendingBlock? composition = null)
    {
        var bytes = new byte[72];
        bytes[0] = (byte)type;
        bytes[1] = (byte)sync;
        bytes[2] = (byte)dataType;
        bytes[3] = bitOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), byteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), invalidationBit);
        return _meta.Add("##CN", new object?[] { next, composition, Text(name), null, conversion, data, null, null }, bytes);
    }

    public PendingBlock Linear(double factor, double offset)
    {
        var data = new byte[24 + 16];
        data[0] = (byte)Mdf4ConversionType.Linear;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), BitConverter.DoubleToInt64Bits(offset));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), BitConverter.DoubleToInt64Bits(factor));
        return _meta.Add("##CC", new object?[4], data);
    }

    public PendingBlock DataBlock(byte[] data) => _meta.Add("##DT", Array.Empty<object?>(), data);

    public byte[] Build(PendingBlock? firstDataGroup, string fileId = "MDF     ", ushort version = 410)
    {
        Header.Links[0] = firstDataGroup;
        using var stream = new MemoryStream();
        var id = new byte[64];
        Encoding.ASCII.GetBytes(fileId, 0, 8, id, 0);
        Encoding.ASCII.GetBytes("4.10    ", 0, 8, id, 8);
        Encoding.ASCII.GetBytes("RawMdf  ", 0, 8, id, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(id.AsSpan(28), version);
        stream.Write(id, 0, id.Length);
        _meta.WriteAll(stream, 64);
        return stream.ToArray();
    }

    /// <summary>The file offset of a block once <see cref="Build"/> has placed it.</summary>
    public static long OffsetOf(PendingBlock block) => block.Offset;
}
