namespace Mdf4Net;

/// <summary>A source to write with a channel or channel group (SI block).</summary>
public sealed class Mdf4SourceDefinition
{
    /// <summary>Creates a source.</summary>
    /// <param name="name">The source name, e.g. an ECU or a bus network name such as <c>CAN1</c>.</param>
    /// <param name="sourceType">The kind of source.</param>
    /// <param name="busType">The bus, if the source is attached to one.</param>
    public Mdf4SourceDefinition(string? name, Mdf4SourceType sourceType, Mdf4BusType busType = Mdf4BusType.None)
    {
        Name = name;
        SourceType = sourceType;
        BusType = busType;
    }

    /// <summary>The source name.</summary>
    public string? Name { get; }

    /// <summary>The kind of source.</summary>
    public Mdf4SourceType SourceType { get; }

    /// <summary>The bus the source is attached to.</summary>
    public Mdf4BusType BusType { get; }

    /// <summary>An optional path, e.g. a tool-specific hierarchy.</summary>
    public string? Path { get; init; }

    /// <summary>An optional comment.</summary>
    public string? Comment { get; init; }

    /// <summary>True if the source is simulated.</summary>
    public bool IsSimulated { get; init; }
}
