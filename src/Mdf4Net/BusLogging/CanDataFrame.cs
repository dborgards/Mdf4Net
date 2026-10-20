using System;

namespace Mdf4Net.BusLogging;

/// <summary>Properties of a CAN frame besides its identifier and data.</summary>
[Flags]
public enum CanFrameFlags : byte
{
    /// <summary>A classic CAN frame with an 11-bit identifier.</summary>
    None = 0,

    /// <summary>29-bit (extended) identifier (IDE).</summary>
    Extended = 1 << 0,

    /// <summary>CAN FD frame (EDL / FDF).</summary>
    FlexibleDataRate = 1 << 1,

    /// <summary>CAN FD bit rate switch (BRS).</summary>
    BitRateSwitch = 1 << 2,

    /// <summary>CAN FD error state indicator (ESI): the sender was error passive.</summary>
    ErrorStateIndicator = 1 << 3,
}

/// <summary>Whether a frame was received or transmitted by the logging node.</summary>
public enum CanDirection : byte
{
    /// <summary>Received.</summary>
    Receive = 0,

    /// <summary>Transmitted.</summary>
    Transmit = 1,
}

/// <summary>One CAN or CAN FD data frame, as logged in an ASAM bus logging channel group.</summary>
public readonly struct CanDataFrame : IEquatable<CanDataFrame>
{
    private static readonly byte[] FdLengths = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64 };

    /// <summary>Creates a frame.</summary>
    /// <param name="timestamp">Seconds relative to the measurement start.</param>
    /// <param name="id">The identifier: 11 bits, or 29 bits with <see cref="CanFrameFlags.Extended"/>.</param>
    /// <param name="data">
    /// The payload: up to 8 bytes, or for CAN FD one of 0..8, 12, 16, 20, 24, 32, 48, 64 bytes.
    /// </param>
    /// <param name="flags">Frame flags.</param>
    /// <param name="busChannel">The bus channel number the frame was logged on.</param>
    /// <param name="direction">Receive or transmit.</param>
    /// <exception cref="ArgumentException">The identifier or the data length is not valid for the flags.</exception>
    public CanDataFrame(double timestamp, uint id, ReadOnlyMemory<byte> data, CanFrameFlags flags = CanFrameFlags.None, byte busChannel = 1, CanDirection direction = CanDirection.Receive)
    {
        uint maxId = (flags & CanFrameFlags.Extended) != 0 ? 0x1FFFFFFFu : 0x7FFu;
        if (id > maxId)
        {
            throw new ArgumentException($"Identifier 0x{id:X} does not fit an {((flags & CanFrameFlags.Extended) != 0 ? "extended" : "11-bit")} frame.", nameof(id));
        }

        bool fd = (flags & CanFrameFlags.FlexibleDataRate) != 0;
        if (!fd && (flags & (CanFrameFlags.BitRateSwitch | CanFrameFlags.ErrorStateIndicator)) != 0)
        {
            throw new ArgumentException("BRS and ESI exist on CAN FD frames only.", nameof(flags));
        }

        int dlc = LengthToDlc(data.Length, fd);
        if (dlc < 0)
        {
            throw new ArgumentException($"{data.Length} bytes is not a valid {(fd ? "CAN FD" : "CAN")} data length.", nameof(data));
        }

        Timestamp = timestamp;
        Id = id;
        Data = data;
        Flags = flags;
        BusChannel = busChannel;
        Direction = direction;
        Dlc = (byte)dlc;
    }

    /// <summary>Seconds relative to the measurement start.</summary>
    public double Timestamp { get; }

    /// <summary>The identifier, without the IDE bit.</summary>
    public uint Id { get; }

    /// <summary>The payload.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Frame flags.</summary>
    public CanFrameFlags Flags { get; }

    /// <summary>The bus channel number.</summary>
    public byte BusChannel { get; }

    /// <summary>Receive or transmit.</summary>
    public CanDirection Direction { get; }

    /// <summary>The data length code (0..15).</summary>
    public byte Dlc { get; }

    /// <summary>True for a 29-bit identifier.</summary>
    public bool IsExtended => (Flags & CanFrameFlags.Extended) != 0;

    /// <summary>True for a CAN FD frame.</summary>
    public bool IsFd => (Flags & CanFrameFlags.FlexibleDataRate) != 0;

    /// <summary>The data length for a data length code: 0..8, or for CAN FD up to 64.</summary>
    public static int DlcToLength(int dlc, bool fd)
    {
        if (dlc is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(dlc));
        }

        return fd ? FdLengths[dlc] : Math.Min(dlc, 8);
    }

    /// <summary>The data length code for a data length, or -1 if the length cannot be encoded.</summary>
    public static int LengthToDlc(int length, bool fd)
    {
        if (length is >= 0 and <= 8)
        {
            return length;
        }

        if (fd)
        {
            for (int dlc = 9; dlc < FdLengths.Length; dlc++)
            {
                if (FdLengths[dlc] == length)
                {
                    return dlc;
                }
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public bool Equals(CanDataFrame other)
        => Timestamp.Equals(other.Timestamp) && Id == other.Id && Flags == other.Flags && BusChannel == other.BusChannel
            && Direction == other.Direction && Data.Span.SequenceEqual(other.Data.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CanDataFrame other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Timestamp.GetHashCode();
            hash = (hash * 397) ^ (int)Id;
            hash = (hash * 397) ^ (int)Flags;
            hash = (hash * 397) ^ BusChannel;
            return (hash * 397) ^ Data.Length;
        }
    }

    /// <summary>Equality.</summary>
    public static bool operator ==(CanDataFrame left, CanDataFrame right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(CanDataFrame left, CanDataFrame right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString()
    {
        string id = IsExtended ? Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : Id.ToString("X3", System.Globalization.CultureInfo.InvariantCulture);
        return $"{Timestamp:F6} {BusChannel} {id} {(IsFd ? "FD " : string.Empty)}[{Data.Length}] {BitConverter.ToString(Data.ToArray())}";
    }
}
