using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests;

public class RoundTripTests
{
    public static IEnumerable<object[]> StorageModes()
    {
        foreach (Mdf4Compression compression in Enum.GetValues(typeof(Mdf4Compression)))
        {
            // A small block size forces DL lists (and HL for DZ); the large one a single block.
            yield return new object[] { compression, 1024 };
            yield return new object[] { compression, 4 * 1024 * 1024 };
        }
    }

    [Theory]
    [MemberData(nameof(StorageModes))]
    public void Every_Data_Type_Reads_Back_As_Written(Mdf4Compression compression, int blockSize)
    {
        const int Count = 1500;
        byte[] bytes = MdfTestFile.Write(
            writer =>
            {
                var definition = new Mdf4ChannelGroupDefinition("Types");
                definition.AddTime();
                Mdf4ChannelDefinition u8 = definition.Add(Mdf4ChannelDefinition.UInt8("u8"));
                Mdf4ChannelDefinition u64 = definition.Add(Mdf4ChannelDefinition.UInt64("u64"));
                Mdf4ChannelDefinition i16 = definition.Add(Mdf4ChannelDefinition.Int16("i16"));
                Mdf4ChannelDefinition i64 = definition.Add(Mdf4ChannelDefinition.Int64("i64"));
                Mdf4ChannelDefinition f32 = definition.Add(Mdf4ChannelDefinition.Float32("f32"));
                Mdf4ChannelDefinition f64 = definition.Add(Mdf4ChannelDefinition.Float64("f64"));
                Mdf4ChannelDefinition half = definition.Add(new Mdf4ChannelDefinition("f16", Mdf4DataType.FloatLe, 16));
                Mdf4ChannelDefinition be = definition.Add(new Mdf4ChannelDefinition("i32_be", Mdf4DataType.SignedIntegerBe, 32));
                Mdf4ChannelDefinition beFloat = definition.Add(new Mdf4ChannelDefinition("f32_be", Mdf4DataType.FloatBe, 32));
                Mdf4ChannelDefinition latin = definition.Add(Mdf4ChannelDefinition.String("latin", 6, Mdf4DataType.StringLatin1));
                Mdf4ChannelDefinition utf16 = definition.Add(Mdf4ChannelDefinition.String("utf16", 12, Mdf4DataType.StringUtf16Le));
                Mdf4ChannelDefinition utf16Be = definition.Add(Mdf4ChannelDefinition.String("utf16be", 12, Mdf4DataType.StringUtf16Be));
                Mdf4ChannelDefinition bytesChannel = definition.Add(Mdf4ChannelDefinition.Bytes("bytes", 3));
                Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);

                for (int i = 0; i < Count; i++)
                {
                    group.BeginRecord(i * 0.01)
                        .Set(u8, (ulong)(i % 256))
                        .Set(u64, ulong.MaxValue - (ulong)i)
                        .Set(i16, (long)(-i))
                        .Set(i64, long.MinValue + i)
                        .Set(f32, i * 0.25)
                        .Set(f64, Math.PI * i)
                        .Set(half, i % 64 * 0.5)
                        .Set(be, (long)(i * -1000))
                        .Set(beFloat, i * 2.0)
                        .Set(latin, "äb" + (i % 10))
                        .Set(utf16, "€" + (i % 100))
                        .Set(utf16Be, "x" + (i % 100))
                        .Set(bytesChannel, new byte[] { (byte)i, 0, (byte)(i >> 8) })
                        .Commit();
                }
            },
            new Mdf4WriterOptions { Compression = compression, DataBlockSize = blockSize });

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4ChannelGroup cg = file.ChannelGroups.Single();
        cg.CountRecords().Should().Be(Count);
        cg.CycleCount.Should().Be(Count);
        cg.ReadTimestamps().Should().Equal(Enumerable.Range(0, Count).Select(i => i * 0.01));

        Values(file, "u8").Should().Equal(Enumerable.Range(0, Count).Select(i => (object)(ulong)(i % 256)));
        Values(file, "u64").Should().Equal(Enumerable.Range(0, Count).Select(i => (object)(ulong.MaxValue - (ulong)i)));
        Values(file, "i16").Should().Equal(Enumerable.Range(0, Count).Select(i => (object)(long)(-i)));
        Values(file, "i64").Should().Equal(Enumerable.Range(0, Count).Select(i => (object)(long.MinValue + i)));
        file.FindChannel("f32")!.ReadDoubles().Should().Equal(Enumerable.Range(0, Count).Select(i => i * 0.25));
        file.FindChannel("f64")!.ReadDoubles().Should().Equal(Enumerable.Range(0, Count).Select(i => Math.PI * i));
        file.FindChannel("f16")!.ReadDoubles().Should().Equal(Enumerable.Range(0, Count).Select(i => i % 64 * 0.5));
        file.FindChannel("i32_be")!.ReadDoubles().Should().Equal(Enumerable.Range(0, Count).Select(i => i * -1000.0));
        file.FindChannel("f32_be")!.ReadDoubles().Should().Equal(Enumerable.Range(0, Count).Select(i => i * 2.0));
        file.FindChannel("latin")!.ReadValues().Should().Equal(Enumerable.Range(0, Count).Select(i => (object)("äb" + (i % 10))));
        file.FindChannel("utf16")!.ReadValues().Should().Equal(Enumerable.Range(0, Count).Select(i => (object)("€" + (i % 100))));
        file.FindChannel("utf16be")!.ReadValues().Should().Equal(Enumerable.Range(0, Count).Select(i => (object)("x" + (i % 100))));
        file.FindChannel("bytes")!.ReadValues().Cast<byte[]>().Select(b => b[0] | (b[2] << 8)).Should().Equal(Enumerable.Range(0, Count));
    }

    [Theory]
    [MemberData(nameof(StorageModes))]
    public void Variable_Length_Values_Read_Back_As_Written(Mdf4Compression compression, int blockSize)
    {
        string Text(int i) => i % 7 == 0 ? string.Empty : new string('ü', i % 300) + i;
        byte[] Blob(int i) => Enumerable.Range(0, i * 13 % 2000).Select(k => (byte)(k + i)).ToArray();

        byte[] bytes = MdfTestFile.Write(
            writer =>
            {
                var definition = new Mdf4ChannelGroupDefinition("Variable");
                definition.AddTime();
                Mdf4ChannelDefinition text = definition.Add(Mdf4ChannelDefinition.VariableLengthString("text"));
                Mdf4ChannelDefinition blob = definition.Add(Mdf4ChannelDefinition.VariableLengthBytes("blob"));
                definition.Add(Mdf4ChannelDefinition.VariableLengthString("unset", Mdf4DataType.StringUtf16Le));
                Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
                for (int i = 0; i < 400; i++)
                {
                    group.BeginRecord(i).Set(text, Text(i)).Set(blob, Blob(i)).Commit();
                }
            },
            new Mdf4WriterOptions { Compression = compression, DataBlockSize = blockSize });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("text")!.ReadValues().Should().Equal(Enumerable.Range(0, 400).Select(i => (object)Text(i)));
        file.FindChannel("blob")!.ReadValues().Cast<byte[]>().Select(b => b.Length).Should().Equal(Enumerable.Range(0, 400).Select(i => Blob(i).Length));
        file.FindChannel("blob")!.ReadValues()[399].Should().BeEquivalentTo(Blob(399));
        file.FindChannel("unset")!.ReadValues().Should().AllBeEquivalentTo(string.Empty);
    }

    [Fact]
    public void Interleaved_Groups_Keep_Their_Own_Records()
    {
        byte[] bytes = MdfTestFile.Write(
            writer =>
            {
                var fastDefinition = new Mdf4ChannelGroupDefinition("Fast");
                fastDefinition.AddTime();
                Mdf4ChannelDefinition fastValue = fastDefinition.Add(Mdf4ChannelDefinition.Int32("fast"));
                var slowDefinition = new Mdf4ChannelGroupDefinition("Slow");
                slowDefinition.AddTime();
                Mdf4ChannelDefinition slowValue = slowDefinition.Add(Mdf4ChannelDefinition.Float64("slow"));
                Mdf4ChannelGroupWriter fast = writer.AddChannelGroup(fastDefinition);
                Mdf4ChannelGroupWriter slow = writer.AddChannelGroup(slowDefinition);

                for (int i = 0; i < 1000; i++)
                {
                    fast.BeginRecord(i * 0.001).Set(fastValue, (long)i).Commit();
                    if (i % 100 == 0)
                    {
                        slow.BeginRecord(i * 0.001).Set(slowValue, i / 100.0).Commit();
                    }
                }
            },
            new Mdf4WriterOptions { DataBlockSize = 1024 });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.DataGroups.Should().HaveCount(2);
        file.ChannelGroups.Select(g => g.Name).Should().Equal("Fast", "Slow");
        file.FindChannel("fast")!.ReadDoubles().Should().Equal(Enumerable.Range(0, 1000).Select(i => (double)i));
        file.FindChannel("slow")!.ReadDoubles().Should().Equal(Enumerable.Range(0, 10).Select(i => (double)i));
        file.FindChannel("slow")!.ChannelGroup.ReadTimestamps().Should().Equal(Enumerable.Range(0, 10).Select(k => k * 100.0 * 0.001));
    }

    [Fact]
    public void Header_File_History_And_Channel_Metadata_Are_Preserved()
    {
        var start = new DateTimeOffset(2026, 10, 20, 9, 30, 15, TimeSpan.FromHours(2));
        var source = new Mdf4SourceDefinition("ECU1", Mdf4SourceType.Ecu, Mdf4BusType.Can) { Path = "Vehicle/ECU1", Comment = "main ECU", IsSimulated = true };
        byte[] bytes = MdfTestFile.Write(
            writer =>
            {
                var definition = new Mdf4ChannelGroupDefinition("Engine") { Comment = "engine signals", Source = source, PathSeparator = '.' };
                definition.AddTime();
                definition.Add(new Mdf4ChannelDefinition("Speed", Mdf4DataType.UnsignedIntegerLe, 16)
                {
                    Unit = "km/h",
                    Comment = "vehicle speed",
                    Precision = 1,
                    MinimumValue = 0,
                    MaximumValue = 250,
                    Flags = Mdf4ChannelFlags.DiscreteValue,
                    Source = source,
                });
                writer.AddChannelGroup(definition).BeginRecord(0).Commit();
            },
            new Mdf4WriterOptions { StartTime = start, Comment = "test <drive> & \"more\"", Version = Mdf4Version.V420, ProgramId = "TestProg", ToolVersion = "9.9.9", UserName = "tester" });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.Version.Should().Be(420);
        file.VersionText.Should().Be("4.20");
        file.ProgramId.Should().Be("TestProg");
        file.IsFinalized.Should().BeTrue();
        file.UnfinalizedFlags.Should().Be(0);
        file.StartTime.Should().Be(start);
        file.StartTime.Offset.Should().Be(TimeSpan.FromHours(2));
        file.Comment.Should().Be("test <drive> & \"more\"");
        file.FileHistory.Should().ContainSingle();
        file.FileHistory[0].ToolId.Should().Be("Mdf4Net");
        file.FileHistory[0].ToolVersion.Should().Be("9.9.9");
        file.FileHistory[0].UserName.Should().Be("tester");
        file.FileHistory[0].Comment.Should().Be("created");

        Mdf4ChannelGroup group = file.ChannelGroups.Single();
        group.Comment.Should().Be("engine signals");
        group.PathSeparator.Should().Be('.');
        group.Source!.Name.Should().Be("ECU1");
        group.Source.Path.Should().Be("Vehicle/ECU1");
        group.Source.Comment.Should().Be("main ECU");
        group.Source.SourceType.Should().Be(Mdf4SourceType.Ecu);
        group.Source.BusType.Should().Be(Mdf4BusType.Can);
        group.Source.IsSimulated.Should().BeTrue();

        Mdf4Channel speed = group.FindChannel("Speed")!;
        speed.Unit.Should().Be("km/h");
        speed.Comment.Should().Be("vehicle speed");
        speed.Precision.Should().Be(1);
        speed.MinimumValue.Should().Be(0);
        speed.MaximumValue.Should().Be(250);
        speed.Flags.Should().HaveFlag(Mdf4ChannelFlags.DiscreteValue);
        speed.Source!.Name.Should().Be("ECU1");
        group.MasterChannel!.Name.Should().Be("t");
        group.MasterChannel.SyncType.Should().Be(Mdf4SyncType.Time);
        group.MasterChannel.Unit.Should().Be("s");
    }

    [Fact]
    public void Conversions_Are_Applied_On_Read()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("Conversions");
            Mdf4ChannelDefinition linear = definition.Add(new Mdf4ChannelDefinition("linear", Mdf4DataType.SignedIntegerLe, 16) { Conversion = Mdf4ConversionDefinition.Linear(0.1, 5) });
            Mdf4ChannelDefinition rational = definition.Add(new Mdf4ChannelDefinition("rational", Mdf4DataType.UnsignedIntegerLe, 8) { Conversion = Mdf4ConversionDefinition.Rational(0, 1, 0, 0, 0, 2) });
            Mdf4ChannelDefinition interpolated = definition.Add(new Mdf4ChannelDefinition("interp", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToValue(new Dictionary<double, double> { [10] = 100, [20] = 300 }, interpolate: true),
            });
            Mdf4ChannelDefinition nearest = definition.Add(new Mdf4ChannelDefinition("nearest", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToValue(new Dictionary<double, double> { [10] = 100, [20] = 300 }, interpolate: false),
            });
            Mdf4ChannelDefinition rangeValue = definition.Add(new Mdf4ChannelDefinition("range_value", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.RangeToValue(new[] { (0.0, 9.0, 1.0), (10.0, 19.0, 2.0) }, -1),
            });
            Mdf4ChannelDefinition text = definition.Add(new Mdf4ChannelDefinition("text", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToText(new Dictionary<double, string> { [0] = "Off", [1] = "On" }, "Error"),
            });
            Mdf4ChannelDefinition noDefault = definition.Add(new Mdf4ChannelDefinition("no_default", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToText(new Dictionary<double, string> { [0] = "Off" }),
            });
            Mdf4ChannelDefinition rangeText = definition.Add(new Mdf4ChannelDefinition("range_text", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.RangeToText(new[] { (0.0, 9.0, "low"), (10.0, 19.0, "high") }, "out"),
            });
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            foreach (int raw in new[] { 0, 1, 9, 10, 15, 19, 20, 25 })
            {
                group.BeginRecord()
                    .Set(linear, (long)(raw - 10))
                    .Set(rational, (ulong)raw)
                    .Set(interpolated, (ulong)raw)
                    .Set(nearest, (ulong)raw)
                    .Set(rangeValue, (ulong)raw)
                    .Set(text, (ulong)raw)
                    .Set(noDefault, (ulong)raw)
                    .Set(rangeText, (ulong)raw)
                    .Commit();
            }
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("linear")!.ReadDoubles().Should().BeEquivalentTo(new[] { 4.0, 4.1, 4.9, 5.0, 5.5, 5.9, 6.0, 6.5 }, o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-12)).WhenTypeIs<double>());
        file.FindChannel("rational")!.ReadDoubles().Should().Equal(0, 0.5, 4.5, 5, 7.5, 9.5, 10, 12.5);
        file.FindChannel("interp")!.ReadDoubles().Should().Equal(100, 100, 100, 100, 200, 280, 300, 300);
        file.FindChannel("nearest")!.ReadDoubles().Should().Equal(100, 100, 100, 100, 100, 300, 300, 300);
        file.FindChannel("range_value")!.ReadDoubles().Should().Equal(1, 1, 1, 2, 2, 2, -1, -1);
        file.FindChannel("text")!.ReadValues().Should().Equal("Off", "On", "Error", "Error", "Error", "Error", "Error", "Error");
        file.FindChannel("no_default")!.ReadValues().Should().Equal("Off", 1.0, 9.0, 10.0, 15.0, 19.0, 20.0, 25.0);
        file.FindChannel("range_text")!.ReadValues().Should().Equal("low", "low", "low", "high", "high", "high", "out", "out");
        file.FindChannel("text")!.ReadRawValues().Should().Equal(0UL, 1UL, 9UL, 10UL, 15UL, 19UL, 20UL, 25UL);

        Action readText = () => file.FindChannel("text")!.ReadDoubles();
        readText.Should().Throw<InvalidOperationException>().WithMessage("*ReadValues*");
    }

    [Fact]
    public void Empty_Conversion_Texts_Are_Texts_Not_Missing_Entries()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            Mdf4ChannelDefinition value = definition.Add(new Mdf4ChannelDefinition("value", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToText(new Dictionary<double, string> { [0] = string.Empty }, string.Empty),
            });
            Mdf4ChannelDefinition range = definition.Add(new Mdf4ChannelDefinition("range", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.RangeToText(new[] { (0.0, 0.0, string.Empty) }, string.Empty),
            });
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            group.BeginRecord().Set(value, 0L).Set(range, 0L).Commit();
            group.BeginRecord().Set(value, 1L).Set(range, 1L).Commit();
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("value")!.ReadValues().Should().Equal(string.Empty, string.Empty);
        file.FindChannel("range")!.ReadValues().Should().Equal(string.Empty, string.Empty);
    }

    [Fact]
    public void Conversion_Tables_Are_Limited_To_What_The_Count_Fields_Hold()
    {
        // cc_val_count is 16 bits: 32,767 pairs (65,534 values) fit, 32,768 pairs do not.
        var largest = Enumerable.Range(0, 32767).ToDictionary(i => (double)i, i => 2.0 * i);
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            Mdf4ChannelDefinition value = definition.Add(new Mdf4ChannelDefinition("value", Mdf4DataType.UnsignedIntegerLe, 16)
            {
                Conversion = Mdf4ConversionDefinition.ValueToValue(largest, interpolate: false),
            });
            writer.AddChannelGroup(definition).BeginRecord().Set(value, 1L).Commit();
        });

        using (Mdf4File file = MdfTestFile.Open(bytes))
        {
            file.FindChannel("value")!.Conversion!.Values.Should().HaveCount(65534);
            file.FindChannel("value")!.ReadDoubles().Should().Equal(2.0);
        }

        var tooMany = Enumerable.Range(0, 32768).ToDictionary(i => (double)i, i => (double)i);
        ((Action)(() => Mdf4ConversionDefinition.ValueToValue(tooMany, interpolate: true))).Should().Throw<ArgumentException>().WithMessage("*65535*");
        var tooManyTexts = Enumerable.Range(0, 65535).ToDictionary(i => (double)i, i => "t");
        ((Action)(() => Mdf4ConversionDefinition.ValueToText(tooManyTexts))).Should().Throw<ArgumentException>().WithMessage("*65535*");
    }

    [Fact]
    public void Structures_Are_Read_As_Compositions()
    {
        Mdf4ChannelDefinition a = Mdf4ChannelDefinition.UInt8("S.a");
        Mdf4ChannelDefinition b = Mdf4ChannelDefinition.Int16("S.b");
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("Structured") { PathSeparator = '.' };
            definition.AddTime();
            definition.Add(new Mdf4ChannelDefinition("S", Mdf4DataType.ByteArray, 8) { Members = new[] { a, b } });
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            group.BeginRecord(0).Set(a, 7L).Set(b, -3L).Commit();
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4Channel structure = file.ChannelGroups.Single().Channels.Single(c => c.Name == "S");
        structure.Children.Select(c => c.Name).Should().Equal("S.a", "S.b");
        structure.Children[1].Parent.Should().BeSameAs(structure);
        structure.ReadValues().Single().Should().BeEquivalentTo(new byte[] { 7, 0xFD, 0xFF });
        file.FindChannel("S.b")!.ReadDoubles().Should().Equal(-3);
    }

    [Fact]
    public void A_Group_Without_Records_And_A_Group_Without_Master_Are_Valid()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var empty = new Mdf4ChannelGroupDefinition("Empty");
            empty.AddTime();
            empty.Add(Mdf4ChannelDefinition.UInt8("nothing"));
            writer.AddChannelGroup(empty);

            var noMaster = new Mdf4ChannelGroupDefinition("NoMaster");
            Mdf4ChannelDefinition value = noMaster.Add(Mdf4ChannelDefinition.UInt8("value"));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(noMaster);
            for (int i = 0; i < 3; i++)
            {
                group.BeginRecord().Set(value, (ulong)(i * 2)).Commit();
            }
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("nothing")!.ReadValues().Should().BeEmpty();
        file.FindChannel("value")!.ChannelGroup.MasterChannel.Should().BeNull();
        file.FindChannel("value")!.ChannelGroup.ReadTimestamps().Should().Equal(0, 1, 2);
        file.FindChannel("value")!.ReadDoubles().Should().Equal(0, 2, 4);
    }

    [Fact]
    public void Raw_Records_Can_Be_Written_Directly()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("Raw");
            definition.Add(Mdf4ChannelDefinition.UInt16("value"));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            group.WriteRecord(new byte[] { 0x34, 0x12 });
            group.WriteRecord(new byte[] { 0xFF, 0xFF });
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("value")!.ReadDoubles().Should().Equal(0x1234, 0xFFFF);
        file.ChannelGroups.Single().ReadRecordBytes().Should().HaveCount(2);
    }

    [Fact]
    public void Writing_Is_Rejected_After_Completion()
    {
        using var stream = new System.IO.MemoryStream();
        using Mdf4Writer writer = Mdf4Writer.Create(stream, leaveOpen: true);
        var definition = new Mdf4ChannelGroupDefinition("G");
        definition.Add(Mdf4ChannelDefinition.UInt8("v"));
        Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
        writer.Complete();

        writer.IsCompleted.Should().BeTrue();
        ((Action)(() => group.BeginRecord())).Should().Throw<InvalidOperationException>();
        ((Action)(() => writer.AddChannelGroup(new Mdf4ChannelGroupDefinition("H")))).Should().Throw<InvalidOperationException>();
        writer.Dispose();
        writer.Dispose();
    }

    [Fact]
    public void A_File_That_Was_Never_Completed_Is_Unfinalized_And_Rejected()
    {
        using var stream = new System.IO.MemoryStream();
        using Mdf4Writer writer = Mdf4Writer.Create(stream, leaveOpen: true);
        var definition = new Mdf4ChannelGroupDefinition("G");
        definition.Add(Mdf4ChannelDefinition.UInt8("v"));
        writer.AddChannelGroup(definition).BeginRecord().Commit();
        writer.Flush();

        // Without Complete the header block is still a zero placeholder.
        byte[] snapshot = stream.ToArray();
        System.Text.Encoding.ASCII.GetString(snapshot, 0, 8).Should().Be("UnFinMF ");
        Action open = () => MdfTestFile.Open(snapshot).Dispose();
        open.Should().Throw<Mdf4FormatException>();
    }

    [Fact]
    public void A_Completion_That_Fails_Can_Be_Repeated_At_Any_Write()
    {
        // Count the writes a successful completion makes, then let each one fail in turn: the
        // writer must not claim completion, must refuse new records, and a second Complete must
        // still produce the whole file.
        int completionWrites;
        using (var probe = new FailingStream())
        {
            using Mdf4Writer writer = Mdf4Writer.Create(probe, Options, leaveOpen: true);
            Fill(writer);
            int before = probe.Writes;
            writer.Complete();
            completionWrites = probe.Writes - before;
        }

        completionWrites.Should().BeGreaterThan(10);
        for (int failAt = 1; failAt <= completionWrites; failAt++)
        {
            using var stream = new FailingStream();
            using Mdf4Writer writer = Mdf4Writer.Create(stream, Options, leaveOpen: true);
            Mdf4ChannelGroupWriter group = Fill(writer);
            stream.FailOnWrite(failAt);

            ((Action)writer.Complete).Should().Throw<System.IO.IOException>($"write {failAt} fails");
            writer.IsCompleted.Should().BeFalse($"write {failAt} failed, so the file is not finalized");
            ((Action)(() => group.BeginRecord())).Should().Throw<InvalidOperationException>();

            if (failAt % 2 == 0)
            {
                writer.Complete();
            }
            else
            {
                writer.Dispose();
            }

            using Mdf4File file = MdfTestFile.Open(stream.ToArray());
            file.IsFinalized.Should().BeTrue($"the retry after write {failAt} finalizes");
            file.FindChannel("v")!.ReadDoubles().Should().Equal(Enumerable.Range(0, 300).Select(i => (double)i), $"write {failAt}");
            file.FindChannel("text")!.ReadValues().Should().Equal(Enumerable.Range(0, 300).Select(i => (object)("t" + i)), $"write {failAt}");
            AssertListOffsetsMatchBlockLengths(file, $"write {failAt}");
        }

        static Mdf4ChannelGroupWriter Fill(Mdf4Writer writer)
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            definition.AddTime();
            Mdf4ChannelDefinition value = definition.Add(Mdf4ChannelDefinition.UInt16("v"));
            Mdf4ChannelDefinition text = definition.Add(Mdf4ChannelDefinition.VariableLengthString("text"));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            for (int i = 0; i < 300; i++)
            {
                group.BeginRecord(i * 0.01).Set(value, (long)i).Set(text, "t" + i).Commit();
            }

            return group;
        }
    }

    [Fact]
    public void A_Commit_That_Fails_On_A_Block_Write_Can_Be_Repeated()
    {
        // A block write fails in the middle of a recording: the failed commit leaves the sinks
        // unchanged, so repeating it continues the file without a gap or shifted offsets.
        using var stream = new FailingStream();
        int failures = 0;
        using (Mdf4Writer writer = Mdf4Writer.Create(stream, Options, leaveOpen: true))
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            definition.AddTime();
            Mdf4ChannelDefinition value = definition.Add(Mdf4ChannelDefinition.UInt16("v"));
            Mdf4ChannelDefinition text = definition.Add(Mdf4ChannelDefinition.VariableLengthString("text"));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            for (int i = 0; i < 600; i++)
            {
                if (i % 97 == 50)
                {
                    stream.FailOnWrite(1 + (i % 3));
                }

                Mdf4RecordBuilder record = group.BeginRecord(i * 0.01).Set(value, (long)i).Set(text, "t" + i);
                try
                {
                    record.Commit();
                }
                catch (System.IO.IOException)
                {
                    failures++;
                    record.Commit();
                }
            }
        }

        failures.Should().BeGreaterThan(0, "the test is only meaningful if writes actually failed");
        using Mdf4File file = MdfTestFile.Open(stream.ToArray());
        file.FindChannel("v")!.ReadDoubles().Should().Equal(Enumerable.Range(0, 600).Select(i => (double)i));
        file.FindChannel("text")!.ReadValues().Should().Equal(Enumerable.Range(0, 600).Select(i => (object)("t" + i)));
        AssertListOffsetsMatchBlockLengths(file, "after failed commits");
    }

    /// <summary>
    /// Each <c>dl_offset</c> must be the position of its block in the logical data stream. The
    /// reader here does not need them, other readers may, so they are checked separately.
    /// </summary>
    private static void AssertListOffsetsMatchBlockLengths(Mdf4File file, string because)
    {
        Mdf4Net.Internal.BlockReader reader = file.Reader;
        foreach (long first in file.DataGroups.Select(group => FirstList(reader, group.DataLink)).Where(list => list != 0))
        {
            long expected = 0;
            for (long dl = first; dl != 0;)
            {
                Mdf4Net.Internal.RawBlock list = reader.ReadBlock(dl, "##DL");
                uint count = list.U32(4);
                for (int i = 0; i < count; i++)
                {
                    ((long)list.U64(8 + (i * 8))).Should().Be(expected, $"{because}: dl_offset {i} of the list at 0x{dl:X}");
                    Mdf4Net.Internal.BlockHeader entry = reader.ReadHeader(list.Links[i + 1]);
                    expected += entry.Id == "##DZ"
                        ? (long)BitConverter.ToUInt64(reader.ReadBytes(entry.DataOffset + 8, 8), 0)
                        : entry.DataLength;
                }

                dl = list.Link(0);
            }
        }
    }

    /// <summary>The first DL block behind a data link (through an HL block), or 0 if there is none.</summary>
    private static long FirstList(Mdf4Net.Internal.BlockReader reader, long link)
    {
        if (link != 0 && reader.ReadHeader(link).Id == "##HL")
        {
            link = reader.ReadBlock(link, "##HL").Link(0);
        }

        return link != 0 && reader.ReadHeader(link).Id == "##DL" ? link : 0;
    }

    private static readonly Mdf4WriterOptions Options = new() { Compression = Mdf4Compression.TransposedDeflate, DataBlockSize = 1024 };

    [Fact]
    public void A_Record_Builder_Held_Across_Completion_Does_Not_Write_To_The_Finalized_File()
    {
        using var stream = new System.IO.MemoryStream();
        using Mdf4Writer writer = Mdf4Writer.Create(stream, new Mdf4WriterOptions { DataBlockSize = 1024 }, leaveOpen: true);
        var definition = new Mdf4ChannelGroupDefinition("G");
        Mdf4ChannelDefinition blob = definition.Add(Mdf4ChannelDefinition.VariableLengthBytes("blob"));
        Mdf4RecordBuilder record = writer.AddChannelGroup(definition).BeginRecord();
        writer.Complete();
        long length = stream.Length;

        // A value larger than a block would be written out at once by the signal data sink.
        record.Set(blob, new byte[2048]);
        ((Action)record.Commit).Should().Throw<InvalidOperationException>();
        stream.Length.Should().Be(length);
    }

    [Theory]
    [InlineData("2500-01-02T03:04:05+00:00")]
    [InlineData("1970-01-01T00:00:00+00:00")]
    [InlineData("2554-07-21T23:34:33.7095516+00:00")]
    public void Start_Times_Across_The_Whole_Mdf_Range_Round_Trip(string start)
    {
        DateTimeOffset time = DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        byte[] bytes = MdfTestFile.Write(_ => { }, new Mdf4WriterOptions { StartTime = time });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.StartTime.Should().Be(time);
    }

    [Theory]
    [InlineData("1969-12-31T23:59:59+00:00")]
    [InlineData("2554-07-21T23:34:33.7095517+00:00")]
    [InlineData("2600-01-01T00:00:00+00:00")]
    public void Start_Times_Outside_The_Mdf_Range_Are_Rejected(string start)
    {
        DateTimeOffset time = DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        using var stream = new System.IO.MemoryStream();
        ((Action)(() => Mdf4Writer.Create(stream, new Mdf4WriterOptions { StartTime = time }))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData((byte)0x00, (short)120, 0, "2026-10-20T10:00:00+00:00")]
    [InlineData((byte)0x02, (short)120, 0, "2026-10-20T12:00:00+02:00")]
    [InlineData((byte)0x02, (short)60, 60, "2026-10-20T12:00:00+02:00")]
    [InlineData((byte)0x03, (short)120, 0, "2026-10-20T10:00:00+02:00")]
    [InlineData((byte)0x01, (short)0, 0, "2026-10-20T10:00:00+00:00")]
    public void Header_Time_Flags_Decide_How_The_Time_Stamp_Is_Read(byte flags, short timeZoneMinutes, short dstMinutes, string expected)
    {
        // The HD block starts at 64; its data (after 24 header bytes and six links) at 136:
        // time_ns (u64), tz_offset_min (i16), dst_offset_min (i16), time_flags (u8).
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("G");
            definition.Add(Mdf4ChannelDefinition.UInt8("v"));
            writer.AddChannelGroup(definition);
        });
        var stored = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        ulong ns = (ulong)(stored.UtcTicks - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks) * 100;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(136), ns);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(144), timeZoneMinutes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(146), dstMinutes);
        bytes[148] = flags;

        using Mdf4File file = MdfTestFile.Open(bytes);
        DateTimeOffset expectedTime = DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        file.StartTime.Should().Be(expectedTime);
        file.StartTime.Offset.Should().Be(expectedTime.Offset);
    }

    private static object?[] Values(Mdf4File file, string name) => file.FindChannel(name)!.ReadRawValues();
}
