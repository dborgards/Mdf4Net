using System;
using System.IO;
using AwesomeAssertions;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests;

public class RecordBuilderTests
{
    [Fact]
    public void Out_Of_Range_Values_Are_Rejected_Not_Truncated()
    {
        WithGroup(
            definition => (definition.Add(Mdf4ChannelDefinition.UInt8("u8")), definition.Add(new Mdf4ChannelDefinition("s4", Mdf4DataType.SignedIntegerLe, 4))),
            (group, channels) =>
            {
                Mdf4RecordBuilder record = group.BeginRecord();
                ((Action)(() => record.Set(channels.Item1, 256L))).Should().Throw<ArgumentOutOfRangeException>();
                ((Action)(() => record.Set(channels.Item1, -1L))).Should().Throw<ArgumentOutOfRangeException>();
                ((Action)(() => record.Set(channels.Item1, 256UL))).Should().Throw<ArgumentOutOfRangeException>();
                ((Action)(() => record.Set(channels.Item1, 1.5))).Should().Throw<ArgumentException>();
                ((Action)(() => record.Set(channels.Item1, double.NaN))).Should().Throw<ArgumentException>();
                ((Action)(() => record.Set(channels.Item2, 8L))).Should().Throw<ArgumentOutOfRangeException>();
                ((Action)(() => record.Set(channels.Item2, -9L))).Should().Throw<ArgumentOutOfRangeException>();
                record.Set(channels.Item2, -8L).Set(channels.Item2, 7L).Set(channels.Item1, 255.0);
            });
    }

    [Fact]
    public void Strings_And_Bytes_Must_Fit_Their_Field()
    {
        WithGroup(
            definition => (definition.Add(Mdf4ChannelDefinition.String("s", 3)), definition.Add(Mdf4ChannelDefinition.Bytes("b", 2))),
            (group, channels) =>
            {
                Mdf4RecordBuilder record = group.BeginRecord();
                ((Action)(() => record.Set(channels.Item1, "abcd"))).Should().Throw<ArgumentException>();
                ((Action)(() => record.Set(channels.Item1, "ää"))).Should().Throw<ArgumentException>("ä takes two bytes in UTF-8");
                ((Action)(() => record.Set(channels.Item2, new byte[3]))).Should().Throw<ArgumentException>();
                ((Action)(() => record.Set(channels.Item1, new byte[1]))).Should().Throw<ArgumentException>("a string channel takes strings");
                ((Action)(() => record.Set(channels.Item2, "x"))).Should().Throw<ArgumentException>("a byte channel takes bytes");
                record.Set(channels.Item1, "abc").Set(channels.Item2, new byte[2]);
            });
    }

    [Fact]
    public void A_Channel_Of_Another_Group_Is_Rejected()
    {
        Mdf4ChannelDefinition foreign = Mdf4ChannelDefinition.UInt8("foreign");
        WithGroup(
            definition => definition.Add(Mdf4ChannelDefinition.UInt8("own")),
            (group, _) => ((Action)(() => group.BeginRecord().Set(foreign, 1L))).Should().Throw<ArgumentException>().WithMessage("*does not belong*"));
    }

    [Fact]
    public void SetPhysical_Inverts_Linear_Conversions_With_Rounding()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            Mdf4ChannelDefinition speed = definition.Add(new Mdf4ChannelDefinition("speed", Mdf4DataType.UnsignedIntegerLe, 16) { Conversion = Mdf4ConversionDefinition.Linear(0.1, -40) });
            Mdf4ChannelDefinition plain = definition.Add(Mdf4ChannelDefinition.Float32("plain"));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            group.BeginRecord().SetPhysical(speed, 12.34).SetPhysical(plain, 2.5).Commit();
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("speed")!.ReadRawValues().Should().Equal(523UL);
        file.FindChannel("speed")!.ReadDoubles()[0].Should().BeApproximately(12.3, 1e-9);
        file.FindChannel("plain")!.ReadDoubles().Should().Equal(2.5);
    }

    [Fact]
    public void SetPhysical_Refuses_Conversions_It_Cannot_Invert()
    {
        WithGroup(
            definition => definition.Add(new Mdf4ChannelDefinition("state", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToText(new System.Collections.Generic.Dictionary<double, string> { [0] = "Off" }),
            }),
            (group, state) => ((Action)(() => group.BeginRecord().SetPhysical(state, 0))).Should().Throw<NotSupportedException>());
    }

    [Fact]
    public void BeginRecord_With_A_Master_Value_Needs_A_Master_Channel()
    {
        WithGroup(
            definition => definition.Add(Mdf4ChannelDefinition.UInt8("v")),
            (group, _) => ((Action)(() => group.BeginRecord(1.0))).Should().Throw<InvalidOperationException>());
    }

    [Fact]
    public void An_Uncommitted_Record_Is_Discarded_By_The_Next_BeginRecord()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            Mdf4ChannelDefinition value = definition.Add(Mdf4ChannelDefinition.UInt8("v"));
            Mdf4ChannelDefinition text = definition.Add(Mdf4ChannelDefinition.VariableLengthString("text"));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            group.BeginRecord().Set(value, 9L).Set(text, "discarded");
            group.BeginRecord().Set(value, 1L).Commit();
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("v")!.ReadDoubles().Should().Equal(1);
        file.FindChannel("text")!.ReadValues().Should().Equal(string.Empty);
    }

    [Fact]
    public void Raw_Records_Are_Refused_For_Groups_With_Variable_Length_Channels()
    {
        WithGroup(
            definition => definition.Add(Mdf4ChannelDefinition.VariableLengthBytes("blob")),
            (group, _) => ((Action)(() => group.WriteRecord(new byte[8]))).Should().Throw<InvalidOperationException>());
    }

    [Fact]
    public void Invalid_Definitions_Are_Rejected_When_Added()
    {
        var definition = new Mdf4ChannelGroupDefinition("G");
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.UnsignedIntegerLe, 65)))).Should().Throw<ArgumentException>();
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.FloatLe, 24)))).Should().Throw<ArgumentException>();
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.ByteArray, 12)))).Should().Throw<ArgumentException>();
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.UnsignedIntegerLe, 8) { BitOffset = 3 }))).Should().Throw<ArgumentException>();
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.UnsignedIntegerLe, 8) { ChannelType = Mdf4ChannelType.Master }))).Should().Throw<ArgumentException>();
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.StringUtf8, 64) { Conversion = Mdf4ConversionDefinition.Linear(1) }))).Should().Throw<ArgumentException>();
        ((Action)(() => definition.Add(new Mdf4ChannelDefinition("x", Mdf4DataType.CanOpenDate, 56)))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new Mdf4ChannelDefinition(string.Empty, Mdf4DataType.UnsignedIntegerLe, 8))).Should().Throw<ArgumentException>();
        ((Action)(() => Mdf4ChannelDefinition.String("s", 4, Mdf4DataType.ByteArray))).Should().Throw<ArgumentException>();

        Mdf4ChannelDefinition once = definition.Add(Mdf4ChannelDefinition.UInt8("once"));
        ((Action)(() => definition.Add(once))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Definition_Is_Frozen_Once_Added_To_A_Writer()
    {
        using var stream = new MemoryStream();
        using Mdf4Writer writer = Mdf4Writer.Create(stream, leaveOpen: true);
        var definition = new Mdf4ChannelGroupDefinition("G");
        definition.Add(Mdf4ChannelDefinition.UInt8("v"));
        writer.AddChannelGroup(definition);

        definition.IsFrozen.Should().BeTrue();
        ((Action)(() => definition.Add(Mdf4ChannelDefinition.UInt8("w")))).Should().Throw<InvalidOperationException>();
        ((Action)(() => writer.AddChannelGroup(definition))).Should().Throw<ArgumentException>();
        ((Action)(() => writer.AddChannelGroup(new Mdf4ChannelGroupDefinition("empty")))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Writer_Options_Are_Validated()
    {
        using var stream = new MemoryStream();
        ((Action)(() => Mdf4Writer.Create(stream, new Mdf4WriterOptions { ProgramId = "far too long" }))).Should().Throw<ArgumentException>();
        ((Action)(() => Mdf4Writer.Create(stream, new Mdf4WriterOptions { DataBlockSize = 10 }))).Should().Throw<ArgumentException>();
        ((Action)(() => Mdf4Writer.Create(stream, new Mdf4WriterOptions { Version = (Mdf4Version)400 }))).Should().Throw<ArgumentException>();
        ((Action)(() => Mdf4Writer.Create(new MemoryStream(new byte[10], writable: false)))).Should().Throw<ArgumentException>();
    }

    private static void WithGroup<T>(Func<Mdf4ChannelGroupDefinition, T> define, Action<Mdf4ChannelGroupWriter, T> act)
    {
        using var stream = new MemoryStream();
        using Mdf4Writer writer = Mdf4Writer.Create(stream, leaveOpen: true);
        var definition = new Mdf4ChannelGroupDefinition("G");
        T channels = define(definition);
        act(writer.AddChannelGroup(definition), channels);
    }
}
