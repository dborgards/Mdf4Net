using System;

namespace Mdf4Net;

/// <summary>Options for <see cref="Mdf4Writer"/>.</summary>
public sealed class Mdf4WriterOptions
{
    /// <summary>The MDF version to write. Default <see cref="Mdf4Version.V410"/>.</summary>
    public Mdf4Version Version { get; init; } = Mdf4Version.V410;

    /// <summary>The program identifier in the identification block, at most eight ASCII characters.</summary>
    public string ProgramId { get; init; } = "Mdf4Net";

    /// <summary>
    /// The start of the measurement. Time stamps of a time master channel are seconds relative to
    /// it. Default: the time the writer is created. A non-zero offset is recorded as the time zone.
    /// MDF stores instants from 1970-01-01 to 2554 (nanoseconds as UInt64); others are rejected.
    /// </summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>The header comment.</summary>
    public string? Comment { get; init; }

    /// <summary>How record and signal data is stored. Default <see cref="Mdf4Compression.None"/>.</summary>
    public Mdf4Compression Compression { get; init; } = Mdf4Compression.None;

    /// <summary>
    /// Uncompressed bytes per data block. Data is buffered per channel group up to this size and
    /// then written; memory use is roughly this size times the number of groups. Default 4 MiB.
    /// </summary>
    public int DataBlockSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>The comment of the file history entry. Default <c>created</c>.</summary>
    public string FileHistoryComment { get; init; } = "created";

    /// <summary>The tool name recorded in the file history.</summary>
    public string ToolId { get; init; } = "Mdf4Net";

    /// <summary>The tool vendor recorded in the file history.</summary>
    public string ToolVendor { get; init; } = "Mdf4Net";

    /// <summary>The tool version recorded in the file history. Default: the library version.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>The user recorded in the file history, if any.</summary>
    public string? UserName { get; init; }

    internal void Validate()
    {
        if (Version is not (Mdf4Version.V410 or Mdf4Version.V420))
        {
            throw new ArgumentException($"Unsupported version {Version}.", nameof(Version));
        }

        if (ProgramId is null || ProgramId.Length > 8)
        {
            throw new ArgumentException("ProgramId must have at most eight characters.", nameof(ProgramId));
        }

        foreach (char c in ProgramId)
        {
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException("ProgramId must be printable ASCII.", nameof(ProgramId));
            }
        }

        if (StartTime is DateTimeOffset start && (start < Internal.Time.Earliest || start > Internal.Time.Latest))
        {
            throw new ArgumentOutOfRangeException(nameof(StartTime), start, $"MDF stores start times from {Internal.Time.Earliest:O} to {Internal.Time.Latest:O}.");
        }

        if (DataBlockSize is < 1024 or > 256 * 1024 * 1024)
        {
            throw new ArgumentException("DataBlockSize must be between 1 KiB and 256 MiB.", nameof(DataBlockSize));
        }
    }
}
