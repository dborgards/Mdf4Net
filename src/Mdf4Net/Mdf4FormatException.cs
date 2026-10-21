using System;

namespace Mdf4Net;

/// <summary>
/// Thrown when a file is not a well-formed MDF 4.x file, or uses a construct this library does
/// not read. Every structural problem a reader can detect in untrusted input surfaces as this
/// exception, so callers can catch one type when opening files from an unknown source.
/// </summary>
public sealed class Mdf4FormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public Mdf4FormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public Mdf4FormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a default message.</summary>
    public Mdf4FormatException()
        : base("The file is not a valid MDF 4.x file.")
    {
    }

    /// <summary>The file offset of the block the problem was found in, or <c>-1</c> if unknown.</summary>
    public long BlockOffset { get; private init; } = -1;

    internal static Mdf4FormatException At(long offset, string message)
        => new($"{message} (block at 0x{offset:X})") { BlockOffset = offset };
}
