using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mdf4Net.Internal;

/// <summary>
/// A metadata block that is laid out before it is written: its links may refer to other pending
/// blocks, whose offsets are only known once the whole set has been placed.
/// </summary>
internal sealed class PendingBlock
{
    public PendingBlock(string id, object?[] links, byte[] data)
    {
        Id = id;
        Links = links;
        Data = data;
    }

    public string Id { get; }

    /// <summary>Each entry is null (no link), a <see cref="PendingBlock"/>, or a <see cref="long"/> file offset.</summary>
    public object?[] Links { get; }

    public byte[] Data { get; }

    public long Offset { get; set; } = -1;

    public long Length => BlockReader.HeaderSize + (Links.Length * 8L) + Data.Length;
}

/// <summary>Collects pending metadata blocks and writes them in one pass.</summary>
internal sealed class MetadataBuilder
{
    private readonly List<PendingBlock> _blocks = new();
    private readonly Dictionary<string, PendingBlock> _texts = new(StringComparer.Ordinal);

    public PendingBlock Add(string id, object?[] links, byte[] data)
    {
        var block = new PendingBlock(id, links, data);
        _blocks.Add(block);
        return block;
    }

    /// <summary>A TX block; identical texts share one block.</summary>
    public PendingBlock? Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (_texts.TryGetValue(text!, out PendingBlock? existing))
        {
            return existing;
        }

        PendingBlock block = Add("##TX", Array.Empty<object?>(), ZeroTerminated(text!));
        _texts[text!] = block;
        return block;
    }

    /// <summary>An MD block holding an XML fragment.</summary>
    public PendingBlock Xml(string xml) => Add("##MD", Array.Empty<object?>(), ZeroTerminated(xml));

    /// <summary>Places every block from <paramref name="start"/> on and writes them.</summary>
    public long WriteAll(Stream stream, long start)
    {
        long position = Align(start);
        foreach (PendingBlock block in _blocks)
        {
            block.Offset = position;
            position = Align(position + block.Length);
        }

        foreach (PendingBlock block in _blocks)
        {
            stream.Position = block.Offset;
            byte[] bytes = Serialize(block.Id, ResolveLinks(block.Links), block.Data);
            stream.Write(bytes, 0, bytes.Length);
            WritePadding(stream, block.Offset + bytes.Length);
        }

        return position;
    }

    public static long[] ResolveLinks(object?[] links)
    {
        var resolved = new long[links.Length];
        for (int i = 0; i < links.Length; i++)
        {
            resolved[i] = links[i] switch
            {
                null => 0,
                PendingBlock pending when pending.Offset >= 0 => pending.Offset,
                PendingBlock => throw new InvalidOperationException("A link refers to a block that was not placed."),
                long offset => offset,
                _ => throw new InvalidOperationException("Unexpected link type."),
            };
        }

        return resolved;
    }

    public static byte[] Serialize(string id, long[] links, byte[] data)
    {
        long length = BlockReader.HeaderSize + (links.Length * 8L) + data.Length;
        var bytes = new byte[length];
        WriteHeader(bytes, id, length, links.Length);
        for (int i = 0; i < links.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(BlockReader.HeaderSize + (i * 8), 8), links[i]);
        }

        Buffer.BlockCopy(data, 0, bytes, BlockReader.HeaderSize + (links.Length * 8), data.Length);
        return bytes;
    }

    public static void WriteHeader(byte[] buffer, string id, long length, long linkCount)
    {
        Encoding.ASCII.GetBytes(id, 0, 4, buffer, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8, 8), length);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16, 8), linkCount);
    }

    public static long Align(long position) => (position + 7) & ~7L;

    public static void WritePadding(Stream stream, long end)
    {
        int padding = (int)(Align(end) - end);
        for (int i = 0; i < padding; i++)
        {
            stream.WriteByte(0);
        }
    }

    /// <summary>UTF-8, zero terminated, padded with zeros to a multiple of eight bytes.</summary>
    public static byte[] ZeroTerminated(string text)
    {
        int count = Encoding.UTF8.GetByteCount(text);
        var bytes = new byte[Align(count + 1)];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }
}
