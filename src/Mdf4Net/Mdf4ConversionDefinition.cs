using System;
using System.Collections.Generic;
using System.Linq;

namespace Mdf4Net;

/// <summary>
/// A conversion to write with a channel: how a reader turns the stored raw value into a
/// physical value or a text. Create one with the static factory methods.
/// </summary>
public sealed class Mdf4ConversionDefinition
{
    private Mdf4ConversionDefinition(Mdf4ConversionType type, double[] values, string?[] texts)
    {
        // cc_val_count and cc_ref_count are 16-bit fields.
        if (values.Length > ushort.MaxValue || texts.Length > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"A {type} conversion stores {values.Length} values and {texts.Length} texts; MDF allows at most {ushort.MaxValue} of each, so the table is too large.");
        }

        Type = type;
        Values = values;
        Texts = texts;
    }

    /// <summary>The conversion type.</summary>
    public Mdf4ConversionType Type { get; }

    /// <summary>An optional name for the conversion.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// An optional unit on the conversion. Prefer <see cref="Mdf4ChannelDefinition.Unit"/>, which
    /// readers look at first.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>An optional comment.</summary>
    public string? Comment { get; init; }

    internal double[] Values { get; }

    internal string?[] Texts { get; }

    /// <summary><c>physical = factor * raw + offset</c>.</summary>
    public static Mdf4ConversionDefinition Linear(double factor, double offset = 0)
        => new(Mdf4ConversionType.Linear, new[] { offset, factor }, Array.Empty<string?>());

    /// <summary><c>physical = (p1 x² + p2 x + p3) / (p4 x² + p5 x + p6)</c>.</summary>
    public static Mdf4ConversionDefinition Rational(double p1, double p2, double p3, double p4, double p5, double p6)
        => new(Mdf4ConversionType.Rational, new[] { p1, p2, p3, p4, p5, p6 }, Array.Empty<string?>());

    /// <summary>A value-to-value table, with or without linear interpolation between keys.</summary>
    public static Mdf4ConversionDefinition ValueToValue(IEnumerable<KeyValuePair<double, double>> table, bool interpolate)
    {
        KeyValuePair<double, double>[] entries = (table ?? throw new ArgumentNullException(nameof(table))).OrderBy(e => e.Key).ToArray();
        if (entries.Length == 0)
        {
            throw new ArgumentException("The table needs at least one entry.", nameof(table));
        }

        for (int i = 1; i < entries.Length; i++)
        {
            if (entries[i].Key.CompareTo(entries[i - 1].Key) == 0)
            {
                throw new ArgumentException($"The table has the key {entries[i].Key} twice.", nameof(table));
            }
        }

        if (entries.Any(e => double.IsNaN(e.Key)))
        {
            throw new ArgumentException("A table key cannot be NaN.", nameof(table));
        }

        double[] values = entries.SelectMany(e => new[] { e.Key, e.Value }).ToArray();
        return new(interpolate ? Mdf4ConversionType.ValueToValueInterpolated : Mdf4ConversionType.ValueToValue, values, Array.Empty<string?>());
    }

    /// <summary>A value range to value table; values outside every range map to <paramref name="defaultValue"/>.</summary>
    public static Mdf4ConversionDefinition RangeToValue(IEnumerable<(double Min, double Max, double Value)> ranges, double defaultValue)
    {
        var values = new List<double>();
        foreach ((double min, double max, double value) in ranges ?? throw new ArgumentNullException(nameof(ranges)))
        {
            values.Add(min);
            values.Add(max);
            values.Add(value);
        }

        values.Add(defaultValue);
        return new(Mdf4ConversionType.ValueRangeToValue, values.ToArray(), Array.Empty<string?>());
    }

    /// <summary>
    /// A value-to-text table (an enumeration). A value without an entry maps to
    /// <paramref name="defaultText"/>, or to the raw value if that is null.
    /// </summary>
    public static Mdf4ConversionDefinition ValueToText(IEnumerable<KeyValuePair<double, string>> table, string? defaultText = null)
    {
        KeyValuePair<double, string>[] entries = (table ?? throw new ArgumentNullException(nameof(table))).ToArray();
        double[] keys = entries.Select(e => e.Key).ToArray();
        var texts = new string?[entries.Length + 1];
        for (int i = 0; i < entries.Length; i++)
        {
            texts[i] = entries[i].Value;
        }

        texts[entries.Length] = defaultText;
        return new(Mdf4ConversionType.ValueToText, keys, texts);
    }

    /// <summary>
    /// A value range to text table. A value outside every range maps to
    /// <paramref name="defaultText"/>, or to the raw value if that is null.
    /// </summary>
    public static Mdf4ConversionDefinition RangeToText(IEnumerable<(double Min, double Max, string Text)> ranges, string? defaultText = null)
    {
        var values = new List<double>();
        var texts = new List<string?>();
        foreach ((double min, double max, string text) in ranges ?? throw new ArgumentNullException(nameof(ranges)))
        {
            values.Add(min);
            values.Add(max);
            texts.Add(text);
        }

        texts.Add(defaultText);
        return new(Mdf4ConversionType.ValueRangeToText, values.ToArray(), texts.ToArray());
    }

    /// <summary>Inverts identity and linear conversions; false for anything else.</summary>
    internal bool TryInvert(double physical, out double raw)
    {
        if (Type == Mdf4ConversionType.Linear && !Values[1].Equals(0d))
        {
            raw = (physical - Values[0]) / Values[1];
            return true;
        }

        raw = 0;
        return false;
    }
}
