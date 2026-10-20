namespace Mdf4Net;

/// <summary>
/// Limits that bound what a reader allocates while parsing a file. They exist because every
/// length and count in an MDF file is attacker-controlled when the file comes from an untrusted
/// source; see SECURITY.md. The defaults are generous for real measurement files.
/// </summary>
public sealed class Mdf4ReaderOptions
{
    /// <summary>The defaults.</summary>
    public static Mdf4ReaderOptions Default { get; } = new();

    /// <summary>
    /// Largest metadata block (anything but DT/SD/DZ data) that is read into memory.
    /// Default 16 MiB.
    /// </summary>
    public int MaxMetadataBlockSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// Largest uncompressed size a single DZ block may declare. Default 256 MiB.
    /// </summary>
    public int MaxDecompressedBlockSize { get; init; } = 256 * 1024 * 1024;

    /// <summary>
    /// Largest total of variable-length signal data (SD blocks or a VLSD channel group) that is
    /// loaded for one channel. Default 1 GiB.
    /// </summary>
    public long MaxSignalDataSize { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Largest record (channel and invalidation bytes) of a channel group. Default 16 MiB.</summary>
    public int MaxRecordSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>Deepest nesting of compositions and nested conversions that is followed. Default 32.</summary>
    public int MaxNestingDepth { get; init; } = 32;
}
