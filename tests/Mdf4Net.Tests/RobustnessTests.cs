using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Mdf4Net.BusLogging;
using Mdf4Net.Internal;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests;

/// <summary>
/// The reader's contract for hostile input (SECURITY.md): a malformed file produces
/// <see cref="Mdf4FormatException"/> (or <see cref="NotSupportedException"/> for an unsupported
/// but well-formed construct), never another exception type, a hang, or an allocation driven by
/// a length field.
/// </summary>
public class RobustnessTests
{
    private static readonly Lazy<byte[]> Sample = new(BuildSample);

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 8).Select(i => new object[] { i });

    [Fact]
    public void Every_Truncation_Is_Rejected_Cleanly()
    {
        byte[] full = Sample.Value;
        for (int length = 0; length < full.Length; length += 3)
        {
            ExpectCleanOutcome(full.AsSpan(0, length).ToArray(), $"truncated to {length} bytes");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_Corruption_Is_Rejected_Cleanly(int seed)
    {
        byte[] full = Sample.Value;
        var random = new Random(seed);
        for (int round = 0; round < 250; round++)
        {
            byte[] corrupt = (byte[])full.Clone();
            int flips = 1 + random.Next(6);
            for (int i = 0; i < flips; i++)
            {
                int position = random.Next(corrupt.Length);
                corrupt[position] = random.Next(3) switch
                {
                    0 => (byte)random.Next(256),
                    1 => 0xFF,
                    _ => 0x00,
                };
            }

            ExpectCleanOutcome(corrupt, $"seed {seed}, round {round}");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Corrupt_Unsorted_Files_Are_Rejected_Cleanly(int seed)
    {
        byte[] full = UnsortedReaderTests.BuildUnsorted(2, out _, out _);
        var random = new Random(seed + 1000);
        for (int round = 0; round < 250; round++)
        {
            byte[] corrupt = (byte[])full.Clone();
            corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
            corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
            ExpectCleanOutcome(corrupt, $"seed {seed}, round {round}");
        }
    }

    [Fact]
    public void Corrupted_Link_Fields_Are_Rejected_Cleanly()
    {
        // Point every link of every block at every other block in turn: type confusion, cycles,
        // links into the middle of blocks.
        byte[] full = Sample.Value;
        List<long> blocks = BlockOffsets(full);
        var random = new Random(42);
        foreach (long block in blocks)
        {
            long linkCount = BinaryPrimitives.ReadInt64LittleEndian(full.AsSpan((int)block + 16));
            for (int link = 0; link < linkCount; link++)
            {
                IEnumerable<long> targets = Enumerable.Range(0, 8).Select(_ => blocks[random.Next(blocks.Count)])
                    .Concat(new long[] { block, block + 8, 64, full.Length - 8, long.MaxValue, -64 });
                foreach (long target in targets)
                {
                    byte[] corrupt = (byte[])full.Clone();
                    BinaryPrimitives.WriteInt64LittleEndian(corrupt.AsSpan((int)block + 24 + (link * 8)), target);
                    ExpectCleanOutcome(corrupt, $"link {link} of block 0x{block:X} -> 0x{target:X}");
                }
            }
        }
    }

    [Fact]
    public void A_Channel_Chain_That_Loops_Is_Detected()
    {
        var raw = new RawMdf();
        PendingBlock channel = raw.Channel("loop", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        channel.Links[0] = channel;
        PendingBlock cg = raw.ChannelGroup(0, 0, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, null));

        Action open = () => MdfTestFile.Open(bytes).Dispose();
        open.Should().Throw<Mdf4FormatException>().WithMessage("*loops*");
    }

    [Fact]
    public void Shared_Conversion_References_Are_Parsed_Once()
    {
        // Twenty value-to-text levels, each referring twice to the same next level: expanded per
        // reference that is 2^20 parses from a file of a few kilobytes.
        const int Levels = 20;
        var raw = new RawMdf();
        PendingBlock next = raw.Linear(2, 0);
        for (int level = 0; level < Levels; level++)
        {
            var data = new byte[24 + 8];
            data[0] = (byte)Mdf4ConversionType.ValueToText;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 1);
            next = raw.Block("##CC", new object?[] { null, null, null, null, next, next }, data);
        }

        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: next);
        PendingBlock cg = raw.ChannelGroup(0, 1, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 0 })));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4Conversion? conversion = file.FindChannel("v")!.Conversion;
        for (int level = 0; level < Levels; level++)
        {
            conversion!.References.Should().HaveCount(2);
            conversion.References[0].Should().BeSameAs(conversion.References[1], $"level {level} refers twice to one block");
            conversion = conversion.References[0] as Mdf4Conversion;
        }

        conversion!.Type.Should().Be(Mdf4ConversionType.Linear);
        file.FindChannel("v")!.ReadValues().Should().Equal(0.0);
    }

    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(2, 4, true)]
    [InlineData(32, 40, true)]
    public void Conversion_Chains_Assembled_From_Cached_Parts_Respect_MaxNestingDepth(int maxDepth, int levels, bool rejected)
    {
        // Channel k uses conversion k, which refers to conversion k-1. Read in channel order, each
        // parse finds its successor in the cache and recurses only one level, while the chain
        // itself grows with every channel.
        var raw = new RawMdf();
        PendingBlock previous = raw.Linear(2, 0);
        PendingBlock? channels = raw.Channel("c0", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: previous);
        PendingBlock first = channels;
        for (int level = 1; level < levels; level++)
        {
            var data = new byte[24 + 8];
            data[0] = (byte)Mdf4ConversionType.ValueToText;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 1);
            previous = raw.Block("##CC", new object?[] { null, null, null, null, raw.Text("x"), previous }, data);
            PendingBlock channel = raw.Channel("c" + level, Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: previous);
            channels.Links[0] = channel;
            channels = channel;
        }

        PendingBlock cg = raw.ChannelGroup(0, 1, 1, 0, first);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 3 })));

        Action open = () =>
        {
            using Mdf4File file = MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxNestingDepth = maxDepth });
            MdfTestFile.ReadEverything(file);
        };
        if (rejected)
        {
            open.Should().Throw<Mdf4FormatException>().WithMessage("*MaxNestingDepth*");
        }
        else
        {
            open.Should().NotThrow();
        }
    }

    [Fact]
    public void A_Forged_Transposition_Column_Count_Does_Not_Drive_The_Untransposition()
    {
        // Sixteen DZ blocks with one byte each and a column count of int.MaxValue: no complete
        // row, so nothing is transposed. Iterating over the column count instead costs about a
        // second per block; the bound below leaves an order of magnitude to the fixed code's
        // milliseconds and to a loaded runner.
        const int Blocks = 16;
        var raw = new RawMdf();
        var links = new object?[Blocks + 1];
        var list = new byte[8 + (Blocks * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(4), Blocks);
        for (int i = 0; i < Blocks; i++)
        {
            byte[] compressed = Zlib.Compress(new[] { (byte)i }, 0, 1);
            var fields = new byte[24 + compressed.Length];
            System.Text.Encoding.ASCII.GetBytes("DT", 0, 2, fields, 0);
            fields[2] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(4), int.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(fields.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(fields.AsSpan(16), (ulong)compressed.Length);
            compressed.CopyTo(fields, 24);
            links[i + 1] = raw.Block("##DZ", Array.Empty<object?>(), fields);
            BinaryPrimitives.WriteInt64LittleEndian(list.AsSpan(8 + (i * 8)), i);
        }

        PendingBlock dl = raw.Block("##DL", links, list);
        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock cg = raw.ChannelGroup(0, Blocks, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.Block("##HL", new object?[] { dl }, new byte[] { 0, 0, 1, 0, 0, 0, 0, 0 })));

        using Mdf4File file = MdfTestFile.Open(bytes);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        file.FindChannel("v")!.ReadDoubles().Should().Equal(Enumerable.Range(0, Blocks).Select(i => (double)i));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(2, new byte[] { 1, 0, 2 }, "middle of a record")]
    [InlineData(3, new byte[] { 1, 0, 2, 0 }, "declares 3 records")]
    public void A_Finalized_File_Whose_Data_Ends_Early_Is_A_Format_Error(ulong cycles, byte[] data, string message)
    {
        var raw = new RawMdf();
        PendingBlock value = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 16);
        PendingBlock cg = raw.ChannelGroup(0, cycles, 2, 0, value);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(data)));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Action read = () => file.FindChannel("v")!.ReadDoubles();
        read.Should().Throw<Mdf4FormatException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void An_Unsorted_Finalized_File_Cut_After_A_Record_Id_Is_A_Format_Error()
    {
        var raw = new RawMdf();
        PendingBlock value = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock cg = raw.ChannelGroup(1, 5, 1, 0, value);
        byte[] bytes = raw.Build(raw.DataGroup(1, cg, raw.DataBlock(new byte[] { 1, 7, 1 })));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Action read = () => file.FindChannel("v")!.ReadDoubles();
        read.Should().Throw<Mdf4FormatException>().WithMessage("*middle of a record*");
    }

    [Fact]
    public void A_Value_To_Text_Table_Nesting_A_Text_Input_Conversion_Is_A_Format_Error()
    {
        var raw = new RawMdf();
        var textToValue = new byte[24 + 8];
        textToValue[0] = (byte)Mdf4ConversionType.TextToValue;
        BinaryPrimitives.WriteUInt16LittleEndian(textToValue.AsSpan(6), 1);
        PendingBlock inner = raw.Block("##CC", new object?[4], textToValue);

        var valueToText = new byte[24 + 8];
        valueToText[0] = (byte)Mdf4ConversionType.ValueToText;
        BinaryPrimitives.WriteUInt16LittleEndian(valueToText.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(valueToText.AsSpan(6), 1);
        PendingBlock outer = raw.Block("##CC", new object?[] { null, null, null, null, raw.Text("zero"), inner }, valueToText);

        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: outer);
        PendingBlock cg = raw.ChannelGroup(0, 1, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 3 })));

        Action open = () =>
        {
            using Mdf4File file = MdfTestFile.Open(bytes);
            file.FindChannel("v")!.ReadValues();
        };
        open.Should().Throw<Mdf4FormatException>().WithMessage("*takes a text*");
    }

    [Fact]
    public void Channels_Sharing_Signal_Data_Load_It_Once()
    {
        // Forty VLSD channels point at one SD block. The file budget allows that block once and
        // a little more: loading it per channel would exceed it at the second channel.
        const int Channels = 40;
        byte[] signal = SignalData(64 * 1024);
        var raw = new RawMdf();
        PendingBlock sd = raw.Block("##SD", Array.Empty<object?>(), signal);
        PendingBlock? next = null;
        for (int i = Channels - 1; i >= 0; i--)
        {
            next = raw.Channel("s" + i, Mdf4ChannelType.VariableLength, Mdf4DataType.ByteArray, (uint)(i * 8), 64, next: next, data: sd);
        }

        PendingBlock cg = raw.ChannelGroup(0, 1, Channels * 8, 0, next);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[Channels * 8])));

        using Mdf4File file = MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxSignalDataSize = signal.Length + 1024 });
        foreach (Mdf4Channel channel in file.Channels)
        {
            channel.ReadValues().Single().Should().BeOfType<byte[]>().Which.Should().HaveCount(signal.Length - 4);
        }
    }

    [Fact]
    public void The_Signal_Data_Budget_Covers_The_Whole_File()
    {
        byte[] signal = SignalData(64 * 1024);
        var raw = new RawMdf();
        PendingBlock second = raw.Channel("b", Mdf4ChannelType.VariableLength, Mdf4DataType.ByteArray, 8, 64, data: raw.Block("##SD", Array.Empty<object?>(), signal));
        PendingBlock first = raw.Channel("a", Mdf4ChannelType.VariableLength, Mdf4DataType.ByteArray, 0, 64, next: second, data: raw.Block("##SD", Array.Empty<object?>(), signal));
        PendingBlock cg = raw.ChannelGroup(0, 1, 16, 0, first);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[16])));

        using Mdf4File file = MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxSignalDataSize = signal.Length * 3 / 2 });
        file.FindChannel("a")!.ReadValues().Should().ContainSingle();
        Action second_ = () => file.FindChannel("b")!.ReadValues();
        second_.Should().Throw<Mdf4FormatException>().WithMessage("*MaxSignalDataSize*");
    }

    private static byte[] SignalData(int size)
    {
        // One SD entry: a 4-byte length and the bytes.
        var data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)(size - 4));
        return data;
    }

    [Theory]
    [InlineData(Mdf4ConversionType.Linear, 0, 0)]
    [InlineData(Mdf4ConversionType.Linear, 1, 0)]
    [InlineData(Mdf4ConversionType.Rational, 2, 0)]
    [InlineData(Mdf4ConversionType.ValueToValueInterpolated, 3, 0)]
    [InlineData(Mdf4ConversionType.ValueToValue, 0, 0)]
    [InlineData(Mdf4ConversionType.ValueRangeToValue, 3, 0)]
    [InlineData(Mdf4ConversionType.ValueToText, 2, 2)]
    [InlineData(Mdf4ConversionType.ValueRangeToText, 3, 2)]
    [InlineData(Mdf4ConversionType.TextToValue, 1, 1)]
    [InlineData(Mdf4ConversionType.TextToText, 0, 2)]
    [InlineData(Mdf4ConversionType.TextToText, 1, 1)]
    [InlineData(Mdf4ConversionType.Identity, 1, 0)]
    [InlineData(Mdf4ConversionType.Identity, 0, 1)]
    [InlineData(Mdf4ConversionType.Linear, 2, 1)]
    [InlineData(Mdf4ConversionType.Rational, 6, 1)]
    [InlineData(Mdf4ConversionType.Algebraic, 0, 0)]
    [InlineData(Mdf4ConversionType.Algebraic, 1, 1)]
    [InlineData(Mdf4ConversionType.ValueToValue, 2, 1)]
    [InlineData(Mdf4ConversionType.ValueRangeToValue, 1, 1)]
    [InlineData(Mdf4ConversionType.BitfieldText, 1, 2)]
    [InlineData(Mdf4ConversionType.BitfieldText, 0, 0)]
    public void A_Conversion_Without_The_Parameters_Of_Its_Type_Is_A_Format_Error(Mdf4ConversionType type, int values, int references)
    {
        var raw = new RawMdf();
        var data = new byte[24 + (values * 8)];
        data[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)references);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)values);

        // The references point at a block that is neither a text nor a conversion: the shape is
        // refused before any of them is followed.
        PendingBlock notAText = raw.DataBlock(new byte[8]);
        var links = new object?[4 + references];
        for (int i = 0; i < references; i++)
        {
            links[4 + i] = notAText;
        }

        PendingBlock conversion = raw.Block("##CC", links, data);
        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: conversion);
        PendingBlock cg = raw.ChannelGroup(0, 1, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 7 })));

        Action open = () => MdfTestFile.Open(bytes).Dispose();
        open.Should().Throw<Mdf4FormatException>().WithMessage($"*{type} conversion cannot have*");
    }

    [Theory]
    [InlineData(Mdf4ConversionType.TextToValue, 1, 2, 0)]
    [InlineData(Mdf4ConversionType.TextToText, 3, 0, 1)]
    [InlineData(Mdf4ConversionType.Algebraic, 1, 0, 0)]
    public void A_Text_Input_Or_Formula_Conversion_Referring_To_A_Conversion_Is_A_Format_Error(Mdf4ConversionType type, int references, int values, int conversionAt)
    {
        var raw = new RawMdf();
        var data = new byte[24 + (values * 8)];
        data[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)references);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)values);
        var links = new object?[4 + references];
        for (int i = 0; i < references; i++)
        {
            links[4 + i] = i == conversionAt ? raw.Linear(1, 0) : raw.Text("t" + i);
        }

        PendingBlock conversion = raw.Block("##CC", links, data);
        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.StringLatin1, 0, 8, conversion: conversion);
        PendingBlock cg = raw.ChannelGroup(0, 1, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 0x41 })));

        Action open = () => MdfTestFile.Open(bytes).Dispose();
        open.Should().Throw<Mdf4FormatException>().WithMessage($"*{type} conversion cannot refer to a conversion*");
    }

    [Fact]
    public void A_Group_Of_Virtual_Channels_Yields_Its_Declared_Records()
    {
        byte[] bytes = VirtualGroup(cycles: 4);

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4ChannelGroup group = file.ChannelGroups.Single();
        group.ReadTimestamps().Should().Equal(0.0, 0.5, 1.0, 1.5);
        file.FindChannel("index")!.ReadValues().Should().Equal(1.0, 3.0, 5.0, 7.0);
    }

    [Theory]
    [InlineData(1001UL, 1000L)]
    [InlineData(ulong.MaxValue, 16L * 1024 * 1024)]
    public void A_Virtual_Group_Declaring_More_Records_Than_Allowed_Is_A_Format_Error(ulong cycles, long allowed)
    {
        // No record bytes are stored, so the cycle count alone would decide what is produced.
        byte[] bytes = VirtualGroup(cycles);

        using Mdf4File file = MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxVirtualRecordCount = allowed });
        Action read = () => file.ChannelGroups.Single().ReadTimestamps();
        read.Should().Throw<Mdf4FormatException>().WithMessage("*MaxVirtualRecordCount*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Stored_Channel_In_A_Group_Without_Record_Bytes_Is_A_Format_Error(bool withVirtualMaster)
    {
        // A fixed-length channel of zero bits: no record bytes, and nothing it could be read from.
        var raw = new RawMdf();
        PendingBlock stored = raw.Channel("s", Mdf4ChannelType.FixedLength, Mdf4DataType.StringUtf8, 0, 0);
        PendingBlock first = withVirtualMaster
            ? raw.Channel("t", Mdf4ChannelType.VirtualMaster, Mdf4DataType.UnsignedIntegerLe, 0, 0, next: stored, sync: Mdf4SyncType.Time)
            : stored;
        byte[] bytes = raw.Build(raw.DataGroup(0, raw.ChannelGroup(0, 2, 0, 0, first), null));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Action read = () => file.FindChannel("s")!.ReadValues();
        read.Should().Throw<Mdf4FormatException>().WithMessage("*records hold no bytes*");
    }

    [Theory]
    [InlineData(nameof(Mdf4ReaderOptions.MaxMetadataBlockSize))]
    [InlineData(nameof(Mdf4ReaderOptions.MaxDecompressedBlockSize))]
    [InlineData(nameof(Mdf4ReaderOptions.MaxSignalDataSize))]
    [InlineData(nameof(Mdf4ReaderOptions.MaxRecordSize))]
    [InlineData(nameof(Mdf4ReaderOptions.MaxVirtualRecordCount))]
    [InlineData(nameof(Mdf4ReaderOptions.MaxNestingDepth))]
    public void A_Negative_Reader_Limit_Is_Refused_Instead_Of_Disabling_It(string limit)
    {
        Mdf4ReaderOptions options = limit switch
        {
            nameof(Mdf4ReaderOptions.MaxMetadataBlockSize) => new() { MaxMetadataBlockSize = -1 },
            nameof(Mdf4ReaderOptions.MaxDecompressedBlockSize) => new() { MaxDecompressedBlockSize = -1 },
            nameof(Mdf4ReaderOptions.MaxSignalDataSize) => new() { MaxSignalDataSize = -1 },
            nameof(Mdf4ReaderOptions.MaxRecordSize) => new() { MaxRecordSize = -1 },
            nameof(Mdf4ReaderOptions.MaxVirtualRecordCount) => new() { MaxVirtualRecordCount = -1 },
            _ => new() { MaxNestingDepth = -1 },
        };

        Action open = () => MdfTestFile.Open(VirtualGroup(cycles: 3), options).Dispose();
        open.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(limit);
    }

    private static byte[] VirtualGroup(ulong cycles)
    {
        var raw = new RawMdf();
        PendingBlock index = raw.Channel("index", Mdf4ChannelType.VirtualData, Mdf4DataType.UnsignedIntegerLe, 0, 0, conversion: raw.Linear(2, 1));
        PendingBlock time = raw.Channel("t", Mdf4ChannelType.VirtualMaster, Mdf4DataType.UnsignedIntegerLe, 0, 0, next: index, sync: Mdf4SyncType.Time, conversion: raw.Linear(0.5, 0));
        PendingBlock cg = raw.ChannelGroup(0, cycles, 0, 0, time);
        return raw.Build(raw.DataGroup(0, cg, null));
    }

    [Theory]
    [InlineData(Mdf4DataType.FloatLe, 32)]
    [InlineData(Mdf4DataType.StringUtf8, 32)]
    [InlineData(Mdf4DataType.ByteArray, 32)]
    [InlineData(Mdf4DataType.ComplexLe, 64)]
    public void A_Bit_Offset_On_A_Non_Integer_Channel_Is_A_Format_Error(Mdf4DataType dataType, uint bitCount)
    {
        var raw = new RawMdf();
        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, dataType, 0, bitCount, bitOffset: 3);
        PendingBlock cg = raw.ChannelGroup(0, 1, 8, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, raw.DataBlock(new byte[] { 0, 0, 0xC0, 0x3F, 0, 0, 0, 0 })));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Action read = () => file.FindChannel("v")!.ReadValues();
        read.Should().Throw<Mdf4FormatException>().WithMessage("*bit offset*");
    }

    [Fact]
    public void A_Master_Channel_With_A_Text_Conversion_Is_A_Format_Error_On_Both_Read_Paths()
    {
        var raw = new RawMdf();
        var data = new byte[24 + 8];
        data[0] = (byte)Mdf4ConversionType.ValueToText;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 1);
        PendingBlock text = raw.Block("##CC", new object?[] { null, null, null, null, raw.Text("zero"), raw.Text("other") }, data);

        // A plain group and a CAN_DataFrame group, each with a UInt8 time master that converts to text.
        PendingBlock plainMaster = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.UnsignedIntegerLe, 0, 8, sync: Mdf4SyncType.Time, conversion: text);
        PendingBlock plain = raw.ChannelGroup(0, 1, 1, 0, plainMaster);
        PendingBlock bytesChannel = raw.Channel("CAN_DataFrame.DataBytes", Mdf4ChannelType.FixedLength, Mdf4DataType.ByteArray, 5, 64, flags: Mdf4ChannelFlags.BusEvent);
        PendingBlock id = raw.Channel("CAN_DataFrame.ID", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 1, 29, next: bytesChannel, flags: Mdf4ChannelFlags.BusEvent);
        PendingBlock frame = raw.Channel("CAN_DataFrame", Mdf4ChannelType.FixedLength, Mdf4DataType.ByteArray, 1, 96, flags: Mdf4ChannelFlags.BusEvent, composition: id);
        PendingBlock canMaster = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.UnsignedIntegerLe, 0, 8, next: frame, sync: Mdf4SyncType.Time, conversion: text);
        PendingBlock can = raw.ChannelGroup(0, 1, 13, 0, canMaster, flags: (ushort)Mdf4ChannelGroupFlags.BusEvent);
        PendingBlock canGroup = raw.DataGroup(0, can, raw.DataBlock(new byte[13]));
        byte[] bytes = raw.Build(raw.DataGroup(0, plain, raw.DataBlock(new byte[] { 1 }), next: canGroup));

        using Mdf4File file = MdfTestFile.Open(bytes);
        ((Action)(() => file.ChannelGroups.First().ReadTimestamps())).Should().Throw<Mdf4FormatException>().WithMessage("*does not yield numbers*");
        Mdf4ChannelGroup canGroupRead = file.ChannelGroups.Last();
        canGroupRead.IsCanDataFrameGroup().Should().BeTrue();
        ((Action)(() => canGroupRead.ReadCanDataFrames().ToList())).Should().Throw<Mdf4FormatException>().WithMessage("*does not yield numbers*");
    }

    [Fact]
    public void A_Conversion_That_Refers_To_Itself_Is_Detected()
    {
        var raw = new RawMdf();
        var data = new byte[24 + 8];
        data[0] = (byte)Mdf4ConversionType.ValueToText;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 1);
        PendingBlock loop = raw.Block("##CC", new object?[6], data);
        loop.Links[4] = loop;
        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8, conversion: loop);
        PendingBlock cg = raw.ChannelGroup(0, 0, 1, 0, channel);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, null));

        Action open = () => MdfTestFile.Open(bytes).Dispose();
        open.Should().Throw<Mdf4FormatException>().WithMessage("*refers back to itself*");
    }

    [Fact]
    public void A_Decompression_Bomb_Is_Refused_Before_Inflating()
    {
        var fields = new byte[24 + 16];
        Encoding.ASCII.GetBytes("DT", 0, 2, fields, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(fields.AsSpan(8), 1UL << 40);
        BinaryPrimitives.WriteUInt64LittleEndian(fields.AsSpan(16), 16);

        var raw = new RawMdf();
        PendingBlock channel = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock cg = raw.ChannelGroup(0, 1, 1, 0, channel);
        PendingBlock dz = raw.Block("##DZ", Array.Empty<object?>(), fields);
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, dz));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Action read = () => file.FindChannel("v")!.ReadDoubles();
        read.Should().Throw<Mdf4FormatException>().WithMessage("*MaxDecompressedBlockSize*");
    }

    [Fact]
    public void Allocation_Limits_Are_Honoured()
    {
        byte[] bytes = Sample.Value;

        Action tinyBlocks = () => MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxMetadataBlockSize = 64 }).Dispose();
        tinyBlocks.Should().Throw<Mdf4FormatException>().WithMessage("*MaxMetadataBlockSize*");

        Action tinyRecords = () => MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxRecordSize = 4 }).Dispose();
        tinyRecords.Should().Throw<Mdf4FormatException>().WithMessage("*MaxRecordSize*");

        using Mdf4File file = MdfTestFile.Open(bytes, new Mdf4ReaderOptions { MaxSignalDataSize = 16 });
        Action vlsd = () => file.FindChannel("text")!.ReadValues();
        vlsd.Should().Throw<Mdf4FormatException>().WithMessage("*MaxSignalDataSize*");
    }

    [Fact]
    public void A_Forged_Vlsd_Length_Does_Not_Allocate_What_It_Claims()
    {
        // A VLSD record claiming 1 GiB in a file of a few hundred bytes: the read must fail on the
        // missing data (end of data), not allocate the claimed size first.
        var raw = new RawMdf();
        PendingBlock vlsdGroup = raw.ChannelGroup(2, 0, 0, 0, null, flags: 1);
        PendingBlock text = raw.Channel("text", Mdf4ChannelType.VariableLength, Mdf4DataType.ByteArray, 0, 64, data: vlsdGroup);
        PendingBlock cg = raw.ChannelGroup(1, 1, 8, 0, text, next: vlsdGroup);
        // One sample record (id 1, offset 0), then the forged VLSD record (id 2).
        var data = new byte[1 + 8 + 1 + 4 + 8];
        data[0] = 1;
        data[9] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), 1u << 30);
        PendingBlock dt = raw.DataBlock(data);
        byte[] bytes = raw.Build(raw.DataGroup(1, cg, dt));

        using Mdf4File file = MdfTestFile.Open(bytes);
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        Action read = () => file.FindChannel("text")!.ReadValues();
        read.Should().Throw<Mdf4FormatException>();
#if NET
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(64L * 1024 * 1024);
#endif
    }

    [Fact]
    public void Not_An_Mdf_File_Is_Rejected()
    {
        ((Action)(() => MdfTestFile.Open(Encoding.ASCII.GetBytes(new string('x', 200))).Dispose()))
            .Should().Throw<Mdf4FormatException>().WithMessage("*identification*");

        byte[] mdf3 = (byte[])Sample.Value.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(mdf3.AsSpan(28), 330);
        ((Action)(() => MdfTestFile.Open(mdf3).Dispose())).Should().Throw<Mdf4FormatException>().WithMessage("*330*");
    }

    private static void ExpectCleanOutcome(byte[] bytes, string because)
    {
        try
        {
            using Mdf4File file = MdfTestFile.Open(bytes);
            MdfTestFile.ReadEverything(file);
        }
        catch (Mdf4FormatException)
        {
            // The contract: a malformed file is reported as a format error.
        }
        catch (NotSupportedException)
        {
            // Also within the contract: a well-formed construct the library does not implement.
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{because}: {ex.GetType().Name} escaped the reader: {ex}");
        }
    }

    private static List<long> BlockOffsets(byte[] file)
    {
        var offsets = new List<long>();
        for (int i = 64; i + 24 <= file.Length; i += 8)
        {
            if (file[i] == (byte)'#' && file[i + 1] == (byte)'#')
            {
                offsets.Add(i);
                long length = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(i + 8));
                i += (int)((length + 7) & ~7L) - 8;
            }
        }

        return offsets;
    }

    private static byte[] BuildSample() => MdfTestFile.Write(
        writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("Sample") { Source = new Mdf4SourceDefinition("ECU", Mdf4SourceType.Ecu) };
            definition.AddTime();
            Mdf4ChannelDefinition value = definition.Add(new Mdf4ChannelDefinition("value", Mdf4DataType.SignedIntegerLe, 12) { Conversion = Mdf4ConversionDefinition.Linear(0.5, 1) });
            Mdf4ChannelDefinition state = definition.Add(new Mdf4ChannelDefinition("state", Mdf4DataType.UnsignedIntegerLe, 8)
            {
                Conversion = Mdf4ConversionDefinition.ValueToText(new Dictionary<double, string> { [0] = "a", [1] = "b" }, "c"),
            });
            Mdf4ChannelDefinition text = definition.Add(Mdf4ChannelDefinition.VariableLengthString("text"));
            Mdf4ChannelDefinition fixedText = definition.Add(Mdf4ChannelDefinition.String("fixed", 4));
            Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
            for (int i = 0; i < 40; i++)
            {
                group.BeginRecord(i * 0.1).Set(value, (long)(i - 20)).Set(state, (ulong)(i % 3)).Set(text, "t" + i).Set(fixedText, "f" + (i % 10)).Commit();
            }

            CanDataFrameWriter can = writer.AddCanDataFrameGroup();
            for (int i = 0; i < 10; i++)
            {
                can.Write(i * 0.01, (uint)(0x100 + i), new byte[i % 9], busChannel: 1);
            }
        },
        new Mdf4WriterOptions { Compression = Mdf4Compression.TransposedDeflate, DataBlockSize = 1024, Comment = "robustness" });
}
