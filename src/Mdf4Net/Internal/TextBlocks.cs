using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Mdf4Net.Internal;

/// <summary>The content of a TX or MD block.</summary>
internal sealed class TextContent
{
    public static readonly TextContent Empty = new(null, null, new Dictionary<string, string>(StringComparer.Ordinal));

    public TextContent(string? text, string? xml, IReadOnlyDictionary<string, string> fields)
    {
        Text = text;
        Xml = xml;
        Fields = fields;
    }

    /// <summary>The plain text: a TX block's string, or the &lt;TX&gt; element of an MD block.</summary>
    public string? Text { get; }

    /// <summary>The XML of an MD block, or null for a TX block.</summary>
    public string? Xml { get; }

    /// <summary>Text of the root element's direct child elements, by local name (first wins).</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }
}

internal static class TextBlocks
{
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = 16 * 1024 * 1024,
    };

    public static string? ReadText(BlockReader reader, long link) => Read(reader, link).Text;

    public static TextContent Read(BlockReader reader, long link)
    {
        if (link == 0)
        {
            return TextContent.Empty;
        }

        if (reader.Texts.TryGetValue(link, out TextContent? cached))
        {
            return cached;
        }

        RawBlock block = reader.ReadBlock(link);
        string value = DecodeZeroTerminatedUtf8(block.Data);
        TextContent content = block.Id switch
        {
            "##TX" => new TextContent(value, null, new Dictionary<string, string>(StringComparer.Ordinal)),
            "##MD" => ParseXml(value),
            _ => throw Mdf4FormatException.At(link, $"Expected a TX or MD block, found {block.Id}"),
        };
        reader.Texts[link] = content;
        return content;
    }

    public static string DecodeZeroTerminatedUtf8(byte[] data)
    {
        int end = Array.IndexOf(data, (byte)0);
        if (end < 0)
        {
            end = data.Length;
        }

        return Encoding.UTF8.GetString(data, 0, end);
    }

    internal static TextContent ParseXml(string xml)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), SafeXml);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // Not well-formed: keep the raw content as the text rather than losing it.
            return new TextContent(xml, xml, fields);
        }

        if (document.Root is null)
        {
            return new TextContent(xml, xml, fields);
        }

        foreach (XElement child in document.Root.Elements())
        {
            string name = child.Name.LocalName;
            if (!fields.ContainsKey(name))
            {
                fields[name] = child.Value;
            }
        }

        return new TextContent(fields.TryGetValue("TX", out string? text) ? text : null, xml, fields);
    }
}
