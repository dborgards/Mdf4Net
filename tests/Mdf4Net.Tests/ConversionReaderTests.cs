using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Mdf4Net.Internal;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests;

/// <summary>Conversion behaviour that needs layouts the writer does not produce (nested conversions).</summary>
public class ConversionReaderTests
{
    [Fact]
    public void A_Nested_Range_Table_Keeps_The_Integer_Context()
    {
        // UInt8 raw values through a value-to-text table whose default is a nested
        // range-to-value table [0, 1] -> 10, default 99. For integers the upper bound is
        // inclusive, also when the table is reached through a reference.
        var raw = new RawMdf();
        var rangeData = new byte[24 + 32];
        rangeData[0] = (byte)Mdf4ConversionType.ValueRangeToValue;
        BinaryPrimitives.WriteUInt16LittleEndian(rangeData.AsSpan(6), 4);
        WriteDoubles(rangeData, 24, 0, 1, 10, 99);
        PendingBlock range = raw.Block("##CC", new object?[4], rangeData);

        var textData = new byte[24 + 8];
        textData[0] = (byte)Mdf4ConversionType.ValueToText;
        BinaryPrimitives.WriteUInt16LittleEndian(textData.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(textData.AsSpan(6), 1);
        WriteDoubles(textData, 24, 5);
        PendingBlock text = raw.Block("##CC", new object?[] { null, null, null, null, raw.Text("five"), range }, textData);

        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: text);
        PendingBlock cg = raw.ChannelGroup(0, 4, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 0, 1, 2, 5 })));

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("v")!.ReadValues().Should().Equal(10.0, 10.0, 99.0, "five");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NaN_Stays_NaN_Through_Value_Tables(bool interpolate)
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            Mdf4ChannelDefinition value = definition.Add(new Mdf4ChannelDefinition("v", Mdf4DataType.FloatLe, 64)
            {
                Conversion = Mdf4ConversionDefinition.ValueToValue(new Dictionary<double, double> { [0] = 0, [1] = 10 }, interpolate),
            });
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            foreach (double sample in new[] { 0.0, double.NaN, 1.0 })
            {
                group.BeginRecord().Set(value, sample).Commit();
            }
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        double[] values = file.FindChannel("v")!.ReadDoubles();
        values[0].Should().Be(0);
        double.IsNaN(values[1]).Should().BeTrue("a missing measurement must not become a table value");
        values[2].Should().Be(10);
    }

    private static void WriteDoubles(byte[] target, int offset, params double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(target.AsSpan(offset + (i * 8)), BitConverter.DoubleToInt64Bits(values[i]));
        }
    }
}
