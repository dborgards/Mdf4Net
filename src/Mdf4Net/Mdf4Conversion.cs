using System;
using System.Collections.Generic;
using System.Linq;
using Mdf4Net.Internal;

namespace Mdf4Net;

/// <summary>
/// A channel conversion (CC block): the rule that turns a raw value into a physical value or a
/// text.
/// </summary>
public sealed class Mdf4Conversion
{
    private readonly double[] _values;
    private readonly object?[] _references;

    private Mdf4Conversion(Mdf4ConversionType type, string? name, string? unit, string? comment, double[] values, object?[] references)
    {
        Height = 1 + references.OfType<Mdf4Conversion>().Select(nested => nested.Height).DefaultIfEmpty(0).Max();

        Type = type;
        Name = name;
        Unit = unit;
        Comment = comment;
        _values = values;
        _references = references;
    }

    /// <summary>The conversion type.</summary>
    public Mdf4ConversionType Type { get; }

    /// <summary>Levels of this conversion including all nested ones (1 for a leaf).</summary>
    internal int Height { get; }

    /// <summary>The conversion name, if any.</summary>
    public string? Name { get; }

    /// <summary>The physical unit declared by the conversion, if any.</summary>
    public string? Unit { get; }

    /// <summary>The conversion comment.</summary>
    public string? Comment { get; }

    /// <summary>The numeric parameters (<c>cc_val</c>), as stored.</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>
    /// The references (<c>cc_ref</c>): a <see cref="string"/> for a text, a nested
    /// <see cref="Mdf4Conversion"/>, or null.
    /// </summary>
    public IReadOnlyList<object?> References => _references;

    /// <summary>True if the conversion can produce a text rather than a number.</summary>
    public bool ProducesText => Type is Mdf4ConversionType.ValueToText or Mdf4ConversionType.ValueRangeToText
        or Mdf4ConversionType.TextToText or Mdf4ConversionType.BitfieldText;

    /// <summary>True if this library can apply the conversion.</summary>
    public bool IsSupported => Type <= Mdf4ConversionType.TextToText && Type != Mdf4ConversionType.Algebraic;

    /// <summary>True if the conversion takes a text as input (text-to-value, text-to-text).</summary>
    public bool TakesText => Type is Mdf4ConversionType.TextToValue or Mdf4ConversionType.TextToText;

    /// <summary>
    /// Applies the conversion to a numeric raw value. Returns a <see cref="double"/>, or a
    /// <see cref="string"/> for a text-producing conversion.
    /// </summary>
    /// <param name="raw">The raw value.</param>
    /// <param name="rawIsInteger">
    /// True if the raw value came from an integer channel; range tables then treat the upper bound
    /// as inclusive, as MDF 4 specifies for integers.
    /// </param>
    /// <exception cref="NotSupportedException">The conversion type is not supported.</exception>
    public object Convert(double raw, bool rawIsInteger = false)
    {
        switch (Type)
        {
            case Mdf4ConversionType.Identity:
                return raw;
            case Mdf4ConversionType.Linear:
                return Param(1) * raw + Param(0);
            case Mdf4ConversionType.Rational:
                return ((Param(0) * raw * raw) + (Param(1) * raw) + Param(2)) / ((Param(3) * raw * raw) + (Param(4) * raw) + Param(5));
            case Mdf4ConversionType.ValueToValueInterpolated:
                return Table(raw, interpolate: true);
            case Mdf4ConversionType.ValueToValue:
                return Table(raw, interpolate: false);
            case Mdf4ConversionType.ValueRangeToValue:
                return RangeToValue(raw, rawIsInteger);
            case Mdf4ConversionType.ValueToText:
                return ValueToText(raw, rawIsInteger);
            case Mdf4ConversionType.ValueRangeToText:
                return RangeToText(raw, rawIsInteger);
            case Mdf4ConversionType.TextToValue:
            case Mdf4ConversionType.TextToText:
                throw new InvalidOperationException($"A {Type} conversion takes a text, not a number.");
            default:
                throw new NotSupportedException($"{Type} conversions are not supported.");
        }
    }

    /// <summary>
    /// Applies a text-input conversion (text-to-value or text-to-text). Returns a
    /// <see cref="double"/> or a <see cref="string"/>.
    /// </summary>
    public object Convert(string raw)
    {
        switch (Type)
        {
            case Mdf4ConversionType.Identity:
                return raw;
            case Mdf4ConversionType.TextToValue:
                {
                    int n = _references.Length;
                    for (int i = 0; i < n; i++)
                    {
                        if (_references[i] is string key && key == raw)
                        {
                            return i < _values.Length ? _values[i] : double.NaN;
                        }
                    }

                    return n < _values.Length ? _values[n] : double.NaN;
                }

            case Mdf4ConversionType.TextToText:
                {
                    int pairs = (_references.Length - 1) / 2;
                    for (int i = 0; i < pairs; i++)
                    {
                        if (_references[2 * i] is string key && key == raw)
                        {
                            return _references[(2 * i) + 1] as string ?? raw;
                        }
                    }

                    return _references.Length > 0 && _references[_references.Length - 1] is string fallback ? fallback : raw;
                }

            default:
                throw new InvalidOperationException($"A {Type} conversion takes a number, not a text.");
        }
    }

    /// <summary>Applies a numeric conversion and returns a number; throws if the result is a text.</summary>
    public double ConvertToDouble(double raw, bool rawIsInteger = false)
    {
        object result = Convert(raw, rawIsInteger);
        return result is double d
            ? d
            : throw new InvalidOperationException($"The {Type} conversion produced a text; read the channel with ReadValues().");
    }

    internal static Mdf4Conversion? Read(BlockReader reader, long link, int depth)
    {
        if (link == 0)
        {
            return null;
        }

        if (reader.Conversions.TryGetValue(link, out Mdf4Conversion? cached))
        {
            return cached;
        }

        if (depth > reader.Options.MaxNestingDepth)
        {
            throw Mdf4FormatException.At(link, "Conversions are nested deeper than MaxNestingDepth");
        }

        if (!reader.ActiveConversions.Add(link))
        {
            throw Mdf4FormatException.At(link, "A conversion refers back to itself");
        }

        try
        {
            Mdf4Conversion conversion = Parse(reader, link, depth);

            // The recursion depth alone is not enough: a nested conversion may come from the cache,
            // already parsed for another channel, and stack arbitrarily many levels under this one.
            // The height counts every level, cached or not, and bounds evaluation as well.
            if (conversion.Height > reader.Options.MaxNestingDepth + 1)
            {
                throw Mdf4FormatException.At(link, "Conversions are nested deeper than MaxNestingDepth");
            }

            reader.Conversions[link] = conversion;
            return conversion;
        }
        finally
        {
            reader.ActiveConversions.Remove(link);
        }
    }

    private static Mdf4Conversion Parse(BlockReader reader, long link, int depth)
    {
        RawBlock block = reader.ReadBlock(link, "##CC");
        var type = (Mdf4ConversionType)block.U8(0);
        ushort refCount = block.U16(4);
        ushort valCount = block.U16(6);
        if (block.Links.Length < 4 + refCount)
        {
            throw Mdf4FormatException.At(link, $"CC block declares {refCount} references but has {block.Links.Length - 4}");
        }

        var values = new double[valCount];
        for (int i = 0; i < valCount; i++)
        {
            values[i] = block.F64(24 + (i * 8));
        }

        var references = new object?[refCount];
        for (int i = 0; i < refCount; i++)
        {
            long reference = block.Links[4 + i];
            if (reference == 0)
            {
                continue;
            }

            BlockHeader header = reader.ReadHeader(reference);
            references[i] = header.Id == "##CC"
                ? Read(reader, reference, depth + 1)
                : TextBlocks.ReadText(reader, reference);
        }

        return new Mdf4Conversion(
            type,
            TextBlocks.ReadText(reader, block.Link(0)),
            TextBlocks.ReadText(reader, block.Link(1)),
            TextBlocks.ReadText(reader, block.Link(2)),
            values,
            references);
    }

    private double Param(int index) => index < _values.Length ? _values[index] : 0;

    private double Table(double raw, bool interpolate)
    {
        int pairs = _values.Length / 2;
        if (pairs == 0 || double.IsNaN(raw))
        {
            // NaN compares false with every key; it stays NaN rather than becoming a table value.
            return raw;
        }

        double Key(int i) => _values[2 * i];
        double Val(int i) => _values[(2 * i) + 1];

        if (raw <= Key(0))
        {
            return Val(0);
        }

        if (raw >= Key(pairs - 1))
        {
            return Val(pairs - 1);
        }

        for (int i = 1; i < pairs; i++)
        {
            if (raw <= Key(i))
            {
                double k0 = Key(i - 1), k1 = Key(i);
                if (raw.Equals(k1))
                {
                    return Val(i);
                }

                if (interpolate)
                {
                    return Val(i - 1) + ((Val(i) - Val(i - 1)) * (raw - k0) / (k1 - k0));
                }

                // Nearest key; a tie goes to the lower one.
                return raw - k0 <= k1 - raw ? Val(i - 1) : Val(i);
            }
        }

        return Val(pairs - 1);
    }

    private double RangeToValue(double raw, bool rawIsInteger)
    {
        int ranges = (_values.Length - 1) / 3;
        for (int i = 0; i < ranges; i++)
        {
            if (InRange(raw, _values[3 * i], _values[(3 * i) + 1], rawIsInteger))
            {
                return _values[(3 * i) + 2];
            }
        }

        return _values.Length > 0 ? _values[_values.Length - 1] : raw;
    }

    private object ValueToText(double raw, bool rawIsInteger)
    {
        int n = _values.Length;
        for (int i = 0; i < n && i < _references.Length; i++)
        {
            if (_values[i].Equals(raw))
            {
                return Resolve(_references[i], raw, rawIsInteger);
            }
        }

        return Resolve(n < _references.Length ? _references[n] : null, raw, rawIsInteger);
    }

    private object RangeToText(double raw, bool rawIsInteger)
    {
        int n = _values.Length / 2;
        for (int i = 0; i < n && i < _references.Length; i++)
        {
            if (InRange(raw, _values[2 * i], _values[(2 * i) + 1], rawIsInteger))
            {
                return Resolve(_references[i], raw, rawIsInteger);
            }
        }

        return Resolve(n < _references.Length ? _references[n] : null, raw, rawIsInteger);
    }

    private static bool InRange(double raw, double min, double max, bool rawIsInteger)
        => rawIsInteger ? raw >= min && raw <= max : raw >= min && raw < max;

    private static object Resolve(object? reference, double raw, bool rawIsInteger) => reference switch
    {
        string text => text,
        Mdf4Conversion nested => nested.Convert(raw, rawIsInteger),
        _ => raw,
    };
}
