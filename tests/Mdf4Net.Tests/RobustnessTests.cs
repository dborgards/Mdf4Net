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
        }
        catch (NotSupportedException)
        {
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
