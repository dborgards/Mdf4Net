using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>Where a channel or channel group was acquired from (SI block).</summary>
public sealed class Mdf4SourceInformation
{
    private Mdf4SourceInformation(string? name, string? path, string? comment, Mdf4SourceType sourceType, Mdf4BusType busType, bool isSimulated)
    {
        Name = name;
        Path = path;
        Comment = comment;
        SourceType = sourceType;
        BusType = busType;
        IsSimulated = isSimulated;
    }

    /// <summary>The source name, e.g. an ECU or a bus network name.</summary>
    public string? Name { get; }

    /// <summary>The source path, e.g. a tool-specific hierarchy.</summary>
    public string? Path { get; }

    /// <summary>The source comment.</summary>
    public string? Comment { get; }

    /// <summary>The kind of source.</summary>
    public Mdf4SourceType SourceType { get; }

    /// <summary>The bus the source is attached to.</summary>
    public Mdf4BusType BusType { get; }

    /// <summary>True if the source was simulated.</summary>
    public bool IsSimulated { get; }

    internal static Mdf4SourceInformation? Read(BlockReader reader, long link)
    {
        if (link == 0)
        {
            return null;
        }

        if (reader.Sources.TryGetValue(link, out Mdf4SourceInformation? cached))
        {
            return cached;
        }

        RawBlock block = reader.ReadBlock(link, "##SI");
        var source = new Mdf4SourceInformation(
            TextBlocks.ReadText(reader, block.Link(0)),
            TextBlocks.ReadText(reader, block.Link(1)),
            TextBlocks.ReadText(reader, block.Link(2)),
            (Mdf4SourceType)block.U8(0),
            (Mdf4BusType)block.U8(1),
            (block.U8(2) & 0x01) != 0);
        reader.Sources[link] = source;
        return source;
    }
}
