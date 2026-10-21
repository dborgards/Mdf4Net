using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// Assembles one record of a channel group. Obtained from
/// <see cref="Mdf4ChannelGroupWriter.BeginRecord()"/>; set values with the <c>Set</c> methods and
/// finish with <see cref="Commit"/>. Values not set are zero (or empty).
/// </summary>
/// <remarks>
/// The <c>Set</c> methods take the <em>raw</em> value, i.e. before the channel's conversion is
/// applied. <see cref="SetPhysical"/> inverts an identity or linear conversion for you.
/// Out-of-range values are rejected rather than truncated.
/// </remarks>
public sealed class Mdf4RecordBuilder
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    private readonly Mdf4ChannelGroupWriter _group;
    private readonly byte[] _record;
    private readonly Dictionary<ChannelLayout, byte[]> _variable = new();

    internal Mdf4RecordBuilder(Mdf4ChannelGroupWriter group)
    {
        _group = group;
        _record = new byte[group.RecordSize];
    }

    /// <summary>Sets an integer value on an integer or float channel.</summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, long value)
    {
        ChannelLayout layout = _group.Layout(channel);
        if (channel.IsFloat)
        {
            BitField.WriteFloat(_record, layout.ByteOffset, layout.BitCount, channel.IsBigEndian, value);
            return this;
        }

        RequireInteger(channel);
        if (channel.IsSigned)
        {
            CheckSignedRange(channel, layout.BitCount, value);
        }
        else if (value < 0 || (ulong)value > BitField.Mask(layout.BitCount))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Channel '{channel.Name}' holds unsigned {layout.BitCount}-bit values.");
        }

        BitField.WriteUnsigned(_record, layout.ByteOffset, layout.BitOffset, layout.BitCount, channel.IsBigEndian, unchecked((ulong)value));
        return this;
    }

    /// <summary>Sets an unsigned integer value on an integer or float channel.</summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, ulong value)
    {
        ChannelLayout layout = _group.Layout(channel);
        if (channel.IsFloat)
        {
            BitField.WriteFloat(_record, layout.ByteOffset, layout.BitCount, channel.IsBigEndian, value);
            return this;
        }

        RequireInteger(channel);
        if (channel.IsSigned)
        {
            if (value > long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"Channel '{channel.Name}' holds signed values.");
            }

            return Set(channel, (long)value);
        }

        if (value > BitField.Mask(layout.BitCount))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Channel '{channel.Name}' holds unsigned {layout.BitCount}-bit values.");
        }

        BitField.WriteUnsigned(_record, layout.ByteOffset, layout.BitOffset, layout.BitCount, channel.IsBigEndian, value);
        return this;
    }

    /// <summary>
    /// Sets a floating-point value. On an integer channel the value must be a whole number in the
    /// channel's range.
    /// </summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, double value)
    {
        ChannelLayout layout = _group.Layout(channel);
        if (channel.IsFloat)
        {
            BitField.WriteFloat(_record, layout.ByteOffset, layout.BitCount, channel.IsBigEndian, value);
            return this;
        }

        RequireInteger(channel);
        if (double.IsNaN(value) || double.IsInfinity(value) || !Math.Floor(value).Equals(value))
        {
            throw new ArgumentException($"Channel '{channel.Name}' holds integers; {value} is not a whole number.", nameof(value));
        }

        if (value >= 0 && value >= 9.2233720368547758E18)
        {
            return value >= 18446744073709551616.0
                ? throw new ArgumentOutOfRangeException(nameof(value), value, $"Value is out of range for channel '{channel.Name}'.")
                : Set(channel, (ulong)value);
        }

        if (value < -9.2233720368547758E18)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Value is out of range for channel '{channel.Name}'.");
        }

        return Set(channel, (long)value);
    }

    /// <summary>Sets a flag: 1 for true, 0 for false.</summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, bool value) => Set(channel, value ? 1L : 0L);

    /// <summary>
    /// Sets a string on a string channel. A fixed-length field must be large enough for the
    /// encoded text; shorter texts are zero-padded.
    /// </summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        ChannelLayout layout = _group.Layout(channel);
        byte[] encoded = channel.DataType switch
        {
            Mdf4DataType.StringLatin1 => Latin1.GetBytes(value),
            Mdf4DataType.StringUtf8 => Encoding.UTF8.GetBytes(value),
            Mdf4DataType.StringUtf16Le => Encoding.Unicode.GetBytes(value),
            Mdf4DataType.StringUtf16Be => Encoding.BigEndianUnicode.GetBytes(value),
            _ => throw new ArgumentException($"Channel '{channel.Name}' does not hold strings.", nameof(channel)),
        };

        return SetBytes(channel, layout, encoded);
    }

    /// <summary>
    /// Sets the bytes of a byte-array channel (fixed length: zero-padded; variable length: any
    /// length).
    /// </summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, ReadOnlySpan<byte> value)
    {
        ChannelLayout layout = _group.Layout(channel);
        if (channel.DataType is not (Mdf4DataType.ByteArray or Mdf4DataType.MimeSample or Mdf4DataType.MimeStream))
        {
            throw new ArgumentException($"Channel '{channel.Name}' does not hold bytes.", nameof(channel));
        }

        return SetBytes(channel, layout, value.ToArray());
    }

    /// <summary>Sets the bytes of a byte-array channel.</summary>
    public Mdf4RecordBuilder Set(Mdf4ChannelDefinition channel, byte[] value)
        => Set(channel, (ReadOnlySpan<byte>)(value ?? throw new ArgumentNullException(nameof(value))));

    /// <summary>
    /// Sets a physical value: inverts the channel's identity or linear conversion and rounds to
    /// the nearest raw value for an integer channel.
    /// </summary>
    /// <exception cref="NotSupportedException">The channel's conversion cannot be inverted.</exception>
    public Mdf4RecordBuilder SetPhysical(Mdf4ChannelDefinition channel, double value)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        double raw = value;
        if (channel.Conversion is not null && !channel.Conversion.TryInvert(value, out raw))
        {
            throw new NotSupportedException($"The {channel.Conversion.Type} conversion of channel '{channel.Name}' cannot be inverted.");
        }

        return Set(channel, channel.IsInteger ? Math.Round(raw, MidpointRounding.AwayFromZero) : raw);
    }

    /// <summary>Appends the record to the channel group.</summary>
    public void Commit()
    {
        // Before any sink is touched: a builder obtained before Complete must not add signal data
        // to a finalized file.
        _group.ThrowIfWriterCompleted();
        foreach (ChannelLayout layout in _group.VariableLengthLayouts)
        {
            byte[] value = _variable.TryGetValue(layout, out byte[]? bytes) ? bytes : Array.Empty<byte>();
            DataSink signal = layout.SignalData!;
            long offset = signal.Length;
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)value.Length);
            signal.Append(length, value);
            BinaryPrimitives.WriteUInt64LittleEndian(_record.AsSpan(layout.ByteOffset, 8), (ulong)offset);
        }

        _group.Commit(_record);
        Reset();
    }

    internal void Reset()
    {
        Array.Clear(_record, 0, _record.Length);
        _variable.Clear();
    }

    private Mdf4RecordBuilder SetBytes(Mdf4ChannelDefinition channel, ChannelLayout layout, byte[] value)
    {
        if (channel.IsVariableLength)
        {
            _variable[layout] = value;
            return this;
        }

        int field = layout.BitCount / 8;
        if (value.Length > field)
        {
            throw new ArgumentException($"Channel '{channel.Name}' has a {field}-byte field; the value needs {value.Length}.", nameof(value));
        }

        Span<byte> target = _record.AsSpan(layout.ByteOffset, field);
        target.Clear();
        value.CopyTo(target);
        return this;
    }

    private static void RequireInteger(Mdf4ChannelDefinition channel)
    {
        if (!channel.IsInteger)
        {
            throw new ArgumentException($"Channel '{channel.Name}' holds {channel.DataType} values, not numbers.", nameof(channel));
        }
    }

    private static void CheckSignedRange(Mdf4ChannelDefinition channel, int bits, long value)
    {
        if (bits >= 64)
        {
            return;
        }

        long max = (1L << (bits - 1)) - 1;
        long min = -(1L << (bits - 1));
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Channel '{channel.Name}' holds signed {bits}-bit values ({min}..{max}).");
        }
    }
}
