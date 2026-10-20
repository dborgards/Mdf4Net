using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mdf4Net.BusLogging;

namespace Mdf4Net.Tests.Infrastructure;

/// <summary>Helpers to write a file into memory and read it back.</summary>
internal static class MdfTestFile
{
    /// <summary>Runs <paramref name="write"/> against a writer on a memory stream and returns the bytes.</summary>
    public static byte[] Write(Action<Mdf4Writer> write, Mdf4WriterOptions? options = null)
    {
        using var stream = new MemoryStream();
        using (Mdf4Writer writer = Mdf4Writer.Create(stream, options, leaveOpen: true))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    public static Mdf4File Open(byte[] bytes, Mdf4ReaderOptions? options = null)
        => Mdf4File.Open(new MemoryStream(bytes, writable: false), leaveOpen: false, options);

    /// <summary>
    /// Reads everything a consumer could ask for: every channel's values, every group's time
    /// stamps, every CAN frame. Used to drive the reader through a whole file.
    /// </summary>
    public static int ReadEverything(Mdf4File file)
    {
        int samples = 0;
        foreach (Mdf4ChannelGroup group in file.ChannelGroups)
        {
            if (group.IsVariableLengthSignalData)
            {
                continue;
            }

            samples += group.ReadTimestamps().Length;
            foreach (Mdf4Channel channel in group.AllChannels)
            {
                samples += channel.ReadValues().Length;
                samples += channel.ReadRawValues().Length;
                if (channel.IsNumeric && channel.Conversion is null or { ProducesText: false, TakesText: false, IsSupported: true })
                {
                    samples += channel.ReadDoubles().Length;
                }
            }

            if (group.IsCanDataFrameGroup())
            {
                samples += group.ReadCanDataFrames().Count();
            }
        }

        return samples;
    }

    public static string FixturePath(params string[] parts)
    {
        var all = new List<string> { AppContext.BaseDirectory, "Fixtures" };
        all.AddRange(parts);
        return Path.Combine(all.ToArray());
    }
}
