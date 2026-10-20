using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// An MDF 4.x file opened for reading. Metadata (groups, channels, conversions, texts) is read
/// when the file is opened; sample data is read on demand from the channel and channel-group
/// methods, chunk by chunk, so files larger than memory can be processed.
/// </summary>
/// <remarks>
/// An instance holds the underlying stream and is not thread-safe: read from one thread at a
/// time. Malformed input surfaces as <see cref="Mdf4FormatException"/>.
/// </remarks>
public sealed class Mdf4File : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly List<Mdf4DataGroup> _dataGroups = new();
    private readonly List<Mdf4FileHistoryEntry> _fileHistory = new();
    private bool _disposed;

    private Mdf4File(Stream stream, bool leaveOpen, Mdf4ReaderOptions options)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Reader = new BlockReader(stream, options);
    }

    internal BlockReader Reader { get; }

    /// <summary>The version number from the identification block, e.g. <c>410</c> for MDF 4.10.</summary>
    public int Version { get; private set; }

    /// <summary>The version text from the identification block, e.g. <c>4.10</c>.</summary>
    public string VersionText { get; private set; } = string.Empty;

    /// <summary>The program identifier of the writing tool (up to eight characters).</summary>
    public string ProgramId { get; private set; } = string.Empty;

    /// <summary>
    /// False if the writer did not finish the file (identification <c>UnFinMF</c>). Cycle counts
    /// and data lengths of such a file are not trusted: records are read until the data ends.
    /// </summary>
    public bool IsFinalized { get; private set; }

    /// <summary>The standard unfinalized flags (<c>id_unfin_flags</c>); zero for a finalized file.</summary>
    public ushort UnfinalizedFlags { get; private set; }

    /// <summary>Start of the measurement, as an instant (UTC based).</summary>
    public DateTimeOffset StartTime { get; private set; }

    /// <summary>The header comment (the text of an <c>HDcomment</c> or a plain TX block).</summary>
    public string? Comment { get; private set; }

    /// <summary>The raw XML of the header comment, if it is an MD block.</summary>
    public string? CommentXml { get; private set; }

    /// <summary>The file history (FH blocks), oldest first.</summary>
    public IReadOnlyList<Mdf4FileHistoryEntry> FileHistory => _fileHistory;

    /// <summary>The data groups, in file order.</summary>
    public IReadOnlyList<Mdf4DataGroup> DataGroups => _dataGroups;

    /// <summary>Every channel group of every data group, in file order.</summary>
    public IEnumerable<Mdf4ChannelGroup> ChannelGroups => _dataGroups.SelectMany(dg => dg.ChannelGroups);

    /// <summary>Every channel, including composition members, in file order.</summary>
    public IEnumerable<Mdf4Channel> Channels => ChannelGroups.SelectMany(cg => cg.AllChannels);

    /// <summary>Opens a file from disk.</summary>
    public static Mdf4File Open(string path, Mdf4ReaderOptions? options = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            return Open(stream, leaveOpen: false, options);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a file from a readable, seekable stream.</summary>
    /// <param name="stream">The stream; it must support reading and seeking.</param>
    /// <param name="leaveOpen">True to leave the stream open when the file is disposed.</param>
    /// <param name="options">Allocation limits; <see cref="Mdf4ReaderOptions.Default"/> if null.</param>
    public static Mdf4File Open(Stream stream, bool leaveOpen = false, Mdf4ReaderOptions? options = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        var file = new Mdf4File(stream, leaveOpen, options ?? Mdf4ReaderOptions.Default);
        file.Load();
        return file;
    }

    /// <summary>The first channel with this name, optionally restricted to a channel group name.</summary>
    public Mdf4Channel? FindChannel(string name, string? channelGroupName = null)
        => FindChannels(name, channelGroupName).FirstOrDefault();

    /// <summary>All channels with this name, optionally restricted to a channel group name.</summary>
    public IEnumerable<Mdf4Channel> FindChannels(string name, string? channelGroupName = null)
        => Channels.Where(c => c.Name == name && (channelGroupName is null || c.ChannelGroup.Name == channelGroupName));

    /// <summary>Closes the underlying stream unless it was opened with <c>leaveOpen</c>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Mdf4File));
        }
    }

    private void Load()
    {
        if (Reader.FileLength < BlockReader.IdentificationSize + BlockReader.HeaderSize)
        {
            throw new Mdf4FormatException("The file is too short to be an MDF file.");
        }

        byte[] id = Reader.ReadBytes(0, BlockReader.IdentificationSize);
        string fileId = Encoding.ASCII.GetString(id, 0, 8);
        if (fileId == "MDF     ")
        {
            IsFinalized = true;
        }
        else if (fileId == "UnFinMF ")
        {
            IsFinalized = false;
        }
        else
        {
            throw new Mdf4FormatException("The file does not start with an MDF identification block.");
        }

        VersionText = Encoding.ASCII.GetString(id, 8, 8).TrimEnd(' ', '\0');
        ProgramId = Encoding.ASCII.GetString(id, 16, 8).TrimEnd(' ', '\0');
        Version = BinaryPrimitives.ReadUInt16LittleEndian(id.AsSpan(28, 2));
        UnfinalizedFlags = BinaryPrimitives.ReadUInt16LittleEndian(id.AsSpan(60, 2));
        if (Version < 400 || Version >= 500)
        {
            throw new Mdf4FormatException($"MDF version {Version} is not supported; this library reads MDF 4.x.");
        }

        RawBlock hd = Reader.ReadBlock(BlockReader.IdentificationSize, "##HD");
        StartTime = Time.FromBlock(hd, 0);
        TextContent comment = TextBlocks.Read(Reader, hd.Link(5));
        Comment = comment.Text;
        CommentXml = comment.Xml;

        var visited = new HashSet<long>();
        for (long fh = hd.Link(1); fh != 0;)
        {
            BlockReader.Visit(visited, fh, "FH");
            RawBlock block = Reader.ReadBlock(fh, "##FH");
            _fileHistory.Add(new Mdf4FileHistoryEntry(Time.FromBlock(block, 0), TextBlocks.Read(Reader, block.Link(1))));
            fh = block.Link(0);
        }

        int index = 0;
        for (long dg = hd.Link(0); dg != 0;)
        {
            BlockReader.Visit(visited, dg, "DG");
            RawBlock block = Reader.ReadBlock(dg, "##DG");
            _dataGroups.Add(new Mdf4DataGroup(this, block, index++, visited));
            dg = block.Link(0);
        }
    }
}

/// <summary>One entry of the file history: who changed the file, when and with what.</summary>
public sealed class Mdf4FileHistoryEntry
{
    internal Mdf4FileHistoryEntry(DateTimeOffset time, TextContent content)
    {
        Time = time;
        Comment = content.Text;
        ToolId = Field(content, "tool_id");
        ToolVendor = Field(content, "tool_vendor");
        ToolVersion = Field(content, "tool_version");
        UserName = Field(content, "user_name");
    }

    /// <summary>When the change was made.</summary>
    public DateTimeOffset Time { get; }

    /// <summary>The description of the change.</summary>
    public string? Comment { get; }

    /// <summary>The tool that made the change.</summary>
    public string? ToolId { get; }

    /// <summary>The vendor of that tool.</summary>
    public string? ToolVendor { get; }

    /// <summary>The version of that tool.</summary>
    public string? ToolVersion { get; }

    /// <summary>The user who made the change, if recorded.</summary>
    public string? UserName { get; }

    private static string? Field(TextContent content, string name)
        => content.Fields.TryGetValue(name, out string? value) ? value : null;
}
