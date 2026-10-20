using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// Writes an MDF 4.x file. Each channel group gets its own (sorted) data group; records are
/// buffered per group and written in blocks as they fill, so recordings of any length can be
/// written with bounded memory.
/// </summary>
/// <remarks>
/// <para>
/// The file is finalized by <see cref="Complete"/> or <see cref="Dispose"/>: only then are the
/// metadata blocks written and the identification changed from <c>UnFinMF</c> to <c>MDF</c>.
/// A writer that is never completed (process killed) leaves an unfinalized file whose data
/// cannot be recovered by this library.
/// </para>
/// <para>Not thread-safe: use a writer and its groups from one thread at a time.</para>
/// </remarks>
public sealed class Mdf4Writer : IDisposable, IBlockAppender
{
    private const int HeaderBlockLength = BlockReader.HeaderSize + (6 * 8) + 32;
    private const string MdfNamespace = "http://www.asam.net/mdf/v4";

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Mdf4WriterOptions _options;
    private readonly List<Mdf4ChannelGroupWriter> _groups = new();
    private readonly DateTimeOffset _startTime;
    private readonly DateTimeOffset _createdAt;
    private long _end;
    private bool _finalizing;
    private bool _completed;
    private bool _disposed;

    private Mdf4Writer(Stream stream, Mdf4WriterOptions options, bool leaveOpen)
    {
        _stream = stream;
        _options = options;
        _leaveOpen = leaveOpen;
        _createdAt = DateTimeOffset.UtcNow;
        _startTime = options.StartTime ?? _createdAt;

        _stream.Position = 0;
        _stream.SetLength(0);
        byte[] id = Identification(finalized: false);
        _stream.Write(id, 0, id.Length);

        // The HD block is written at the end, when its links are known; reserve its place.
        var placeholder = new byte[HeaderBlockLength];
        _stream.Write(placeholder, 0, placeholder.Length);
        _end = MetadataBuilder.Align(BlockReader.IdentificationSize + HeaderBlockLength);
    }

    /// <summary>The channel groups added so far.</summary>
    public IReadOnlyList<Mdf4ChannelGroupWriter> ChannelGroups => _groups;

    /// <summary>
    /// True once the file was finalized successfully. A <see cref="Complete"/> that failed (for
    /// example on an I/O error) leaves this false; calling <see cref="Complete"/> again retries.
    /// </summary>
    public bool IsCompleted => _completed;

    /// <summary>Creates (or overwrites) a file on disk.</summary>
    public static Mdf4Writer Create(string path, Mdf4WriterOptions? options = null)
    {
        options ??= new Mdf4WriterOptions();
        options.Validate();
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        try
        {
            return new Mdf4Writer(stream, options, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Writes to a stream that supports writing and seeking. Existing content is replaced.</summary>
    public static Mdf4Writer Create(Stream stream, Mdf4WriterOptions? options = null, bool leaveOpen = false)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be writable and seekable.", nameof(stream));
        }

        options ??= new Mdf4WriterOptions();
        options.Validate();
        return new Mdf4Writer(stream, options, leaveOpen);
    }

    /// <summary>
    /// Adds a channel group and returns the writer for its records. The definition is frozen.
    /// Groups can be added at any time before the file is completed.
    /// </summary>
    public Mdf4ChannelGroupWriter AddChannelGroup(Mdf4ChannelGroupDefinition definition)
    {
        ThrowIfCompleted();
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (definition.IsFrozen)
        {
            throw new ArgumentException("This channel group definition was already added to a writer.", nameof(definition));
        }

        if (definition.Channels.Count == 0)
        {
            throw new ArgumentException("A channel group needs at least one channel.", nameof(definition));
        }

        if ((definition.Flags & ~(Mdf4ChannelGroupFlags.BusEvent | Mdf4ChannelGroupFlags.PlainBusEvent)) != 0)
        {
            throw new ArgumentException("Only the BusEvent and PlainBusEvent group flags can be written.", nameof(definition));
        }

        int masters = 0;
        foreach (Mdf4ChannelDefinition channel in definition.Channels)
        {
            masters += channel.ChannelType == Mdf4ChannelType.Master ? 1 : 0;
        }

        if (masters > 1)
        {
            throw new ArgumentException("A channel group has at most one master channel.", nameof(definition));
        }

        definition.Freeze();
        var group = new Mdf4ChannelGroupWriter(this, definition, _options);
        _groups.Add(group);
        return group;
    }

    /// <summary>
    /// Writes every buffered record as a data block and flushes the stream. Calling this often
    /// produces many small blocks; normally blocks are written as they fill.
    /// </summary>
    public void Flush()
    {
        ThrowIfCompleted();
        foreach (Mdf4ChannelGroupWriter group in _groups)
        {
            group.Records.FlushBlock();
            foreach (ChannelLayout layout in group.VariableLengthLayouts)
            {
                layout.SignalData!.FlushBlock();
            }
        }

        _stream.Flush();
    }

    /// <summary>
    /// Finalizes the file: writes the remaining data, the metadata, and the final identification.
    /// The writer cannot be used afterwards. Called by <see cref="Dispose"/> if not called before.
    /// </summary>
    public void Complete()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Mdf4Writer));
        }

        if (_completed)
        {
            return;
        }

        // From the first attempt on, no more records are accepted. The file only counts as
        // completed once the identification says MDF; if an attempt fails (an I/O error), it
        // can be repeated, and Dispose repeats it. Every step below can run again: data sinks
        // keep their state until a block is in the file, and metadata is rewritten in place.
        _finalizing = true;
        var dataLinks = new long[_groups.Count];
        for (int i = 0; i < _groups.Count; i++)
        {
            dataLinks[i] = _groups[i].Records.Finish();
            foreach (ChannelLayout layout in _groups[i].VariableLengthLayouts)
            {
                layout.SignalDataLink = layout.SignalData!.Finish();
            }
        }

        var meta = new MetadataBuilder();
        var sources = new Dictionary<Mdf4SourceDefinition, PendingBlock>();

        PendingBlock fileHistory = meta.Add("##FH", new object?[] { null, meta.Xml(FileHistoryXml()) }, TimeFields(_createdAt, 16));
        PendingBlock? headerComment = string.IsNullOrEmpty(_options.Comment)
            ? null
            : meta.Xml(new XElement(XName.Get("HDcomment", MdfNamespace), new XElement(XName.Get("TX", MdfNamespace), _options.Comment)).ToString(SaveOptions.DisableFormatting));

        PendingBlock? nextDataGroup = null;
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            PendingBlock channelGroup = ChannelGroupBlock(meta, sources, _groups[i]);
            nextDataGroup = meta.Add("##DG", new object?[] { nextDataGroup, channelGroup, dataLinks[i], null }, new byte[8]);
        }

        _end = meta.WriteAll(_stream, _end);

        long[] headerLinks = MetadataBuilder.ResolveLinks(new object?[] { nextDataGroup, fileHistory, null, null, null, headerComment });
        byte[] header = MetadataBuilder.Serialize("##HD", headerLinks, TimeFields(_startTime, 32));
        _stream.Position = BlockReader.IdentificationSize;
        _stream.Write(header, 0, header.Length);

        byte[] id = Identification(finalized: true);
        _stream.Position = 0;
        _stream.Write(id, 0, id.Length);
        _stream.Position = _end;
        _stream.Flush();
        _completed = true;
    }

    /// <summary>Completes the file (if not done yet) and closes the stream unless <c>leaveOpen</c>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Complete();
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    long IBlockAppender.AppendBlock(string id, long[] links, byte[] data, int offset, int count)
        => ((IBlockAppender)this).AppendBlock(id, links, Array.Empty<byte>(), data, offset, count);

    long IBlockAppender.AppendBlock(string id, long[] links, byte[] prefix, byte[] data, int offset, int count)
    {
        long position = _end;
        long length = BlockReader.HeaderSize + (links.Length * 8L) + prefix.Length + count;
        var head = new byte[BlockReader.HeaderSize + (links.Length * 8)];
        MetadataBuilder.WriteHeader(head, id, length, links.Length);
        for (int i = 0; i < links.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(BlockReader.HeaderSize + (i * 8), 8), links[i]);
        }

        _stream.Position = position;
        _stream.Write(head, 0, head.Length);
        _stream.Write(prefix, 0, prefix.Length);
        _stream.Write(data, offset, count);
        MetadataBuilder.WritePadding(_stream, position + length);
        _end = MetadataBuilder.Align(position + length);
        return position;
    }

    internal void ThrowIfCompleted()
    {
        if (_finalizing || _completed || _disposed)
        {
            throw new InvalidOperationException("The writer was completed; nothing more can be written.");
        }
    }

    private byte[] Identification(bool finalized)
    {
        var id = new byte[BlockReader.IdentificationSize];
        Encoding.ASCII.GetBytes(finalized ? "MDF     " : "UnFinMF ", 0, 8, id, 0);
        string version = _options.Version == Mdf4Version.V420 ? "4.20    " : "4.10    ";
        Encoding.ASCII.GetBytes(version, 0, 8, id, 8);
        Encoding.ASCII.GetBytes(_options.ProgramId.PadRight(8), 0, 8, id, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(id.AsSpan(28, 2), (ushort)_options.Version);
        if (!finalized)
        {
            // Cycle counters, the last DT length and the last DL are not up to date.
            BinaryPrimitives.WriteUInt16LittleEndian(id.AsSpan(60, 2), 0x0001 | 0x0004 | 0x0010);
        }

        return id;
    }

    private static byte[] TimeFields(DateTimeOffset time, int size)
    {
        var data = new byte[size];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0, 8), Internal.Time.ToNanoseconds(time));
        if (time.Offset != TimeSpan.Zero)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(8, 2), (short)time.Offset.TotalMinutes);
            data[12] = 0x02; // the offsets are valid
        }

        return data;
    }

    private string FileHistoryXml()
    {
        XName Name(string local) => XName.Get(local, MdfNamespace);
        string version = _options.ToolVersion ?? LibraryVersion();
        var root = new XElement(
            Name("FHcomment"),
            new XElement(Name("TX"), _options.FileHistoryComment),
            new XElement(Name("tool_id"), _options.ToolId),
            new XElement(Name("tool_vendor"), _options.ToolVendor),
            new XElement(Name("tool_version"), version));
        if (!string.IsNullOrEmpty(_options.UserName))
        {
            root.Add(new XElement(Name("user_name"), _options.UserName));
        }

        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static string LibraryVersion()
    {
        string? informational = typeof(Mdf4Writer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        int plus = informational!.IndexOf('+');
        return plus < 0 ? informational : informational.Substring(0, plus);
    }

    private static PendingBlock ChannelGroupBlock(MetadataBuilder meta, Dictionary<Mdf4SourceDefinition, PendingBlock> sources, Mdf4ChannelGroupWriter group)
    {
        Mdf4ChannelGroupDefinition definition = group.Definition;
        PendingBlock? firstChannel = ChannelChain(meta, sources, group.TopLevel);

        var data = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8, 8), (ulong)group.RecordCount);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16, 2), (ushort)definition.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18, 2), definition.PathSeparator ?? '\0');
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)group.RecordSize);

        return meta.Add(
            "##CG",
            new object?[]
            {
                null,
                firstChannel,
                meta.Text(definition.Name),
                Source(meta, sources, definition.Source),
                null,
                meta.Text(definition.Comment),
            },
            data);
    }

    private static PendingBlock? ChannelChain(MetadataBuilder meta, Dictionary<Mdf4SourceDefinition, PendingBlock> sources, IReadOnlyList<ChannelLayout> layouts)
    {
        PendingBlock? next = null;
        for (int i = layouts.Count - 1; i >= 0; i--)
        {
            next = ChannelBlock(meta, sources, layouts[i], next);
        }

        return next;
    }

    private static PendingBlock ChannelBlock(MetadataBuilder meta, Dictionary<Mdf4SourceDefinition, PendingBlock> sources, ChannelLayout layout, PendingBlock? next)
    {
        Mdf4ChannelDefinition channel = layout.Definition;
        PendingBlock? composition = ChannelChain(meta, sources, layout.Members);

        Mdf4ChannelFlags flags = channel.Flags;
        if (channel.Precision is not null)
        {
            flags |= Mdf4ChannelFlags.PrecisionValid;
        }

        if (channel.MinimumValue is not null && channel.MaximumValue is not null)
        {
            flags |= Mdf4ChannelFlags.ValueRangeValid;
        }

        var data = new byte[72];
        data[0] = (byte)channel.ChannelType;
        data[1] = (byte)channel.SyncType;
        data[2] = (byte)channel.DataType;
        data[3] = (byte)layout.BitOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), (uint)layout.ByteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)layout.BitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), (uint)flags);
        data[20] = channel.Precision ?? 0;
        if ((flags & Mdf4ChannelFlags.ValueRangeValid) != 0)
        {
            WriteDouble(data, 24, channel.MinimumValue!.Value);
            WriteDouble(data, 32, channel.MaximumValue!.Value);
        }

        return meta.Add(
            "##CN",
            new object?[]
            {
                next,
                composition,
                meta.Text(channel.Name),
                Source(meta, sources, channel.Source),
                Conversion(meta, channel.Conversion),
                channel.IsVariableLength ? layout.SignalDataLink : null,
                meta.Text(channel.Unit),
                meta.Text(channel.Comment),
            },
            data);
    }

    private static PendingBlock? Conversion(MetadataBuilder meta, Mdf4ConversionDefinition? conversion)
    {
        if (conversion is null)
        {
            return null;
        }

        var links = new object?[4 + conversion.Texts.Length];
        links[0] = meta.Text(conversion.Name);
        links[1] = meta.Text(conversion.Unit);
        links[2] = meta.Text(conversion.Comment);
        for (int i = 0; i < conversion.Texts.Length; i++)
        {
            links[4 + i] = meta.ConversionText(conversion.Texts[i]);
        }

        if (conversion.Values.Length > ushort.MaxValue || conversion.Texts.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("A conversion has more values or texts than cc_val_count / cc_ref_count can hold.");
        }

        var data = new byte[24 + (conversion.Values.Length * 8)];
        data[0] = (byte)conversion.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), (ushort)conversion.Texts.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), (ushort)conversion.Values.Length);
        for (int i = 0; i < conversion.Values.Length; i++)
        {
            WriteDouble(data, 24 + (i * 8), conversion.Values[i]);
        }

        return meta.Add("##CC", links, data);
    }

    private static PendingBlock? Source(MetadataBuilder meta, Dictionary<Mdf4SourceDefinition, PendingBlock> sources, Mdf4SourceDefinition? source)
    {
        if (source is null)
        {
            return null;
        }

        if (sources.TryGetValue(source, out PendingBlock? existing))
        {
            return existing;
        }

        var data = new byte[8];
        data[0] = (byte)source.SourceType;
        data[1] = (byte)source.BusType;
        data[2] = (byte)(source.IsSimulated ? 1 : 0);
        PendingBlock block = meta.Add("##SI", new object?[] { meta.Text(source.Name), meta.Text(source.Path), meta.Text(source.Comment) }, data);
        sources[source] = block;
        return block;
    }

    private static void WriteDouble(byte[] data, int offset, double value)
        => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), BitConverter.DoubleToInt64Bits(value));
}
