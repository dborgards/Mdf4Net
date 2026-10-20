using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Mdf4Net.Internal;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests;

/// <summary>
/// Reading layouts that loggers produce and the writer does not: unsorted data groups with
/// interleaved records of several channel groups, a VLSD channel group, invalidation bits,
/// maximum-length and virtual channels.
/// </summary>
public class UnsortedReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Interleaved_Records_Are_Assigned_To_Their_Groups(byte recordIdSize)
    {
        byte[] bytes = BuildUnsorted(recordIdSize, out List<(double Time, byte Value, bool Valid, byte[] Frame, string Text)> expected, out int virtualCount);

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4DataGroup group = file.DataGroups.Single();
        group.IsUnsorted.Should().BeTrue();
        group.RecordIdSize.Should().Be(recordIdSize);
        group.ChannelGroups.Should().HaveCount(3);
        group.ChannelGroups[1].IsVariableLengthSignalData.Should().BeTrue();

        Mdf4ChannelGroup samples = group.ChannelGroups[0];
        samples.ReadTimestamps().Should().Equal(expected.Select(e => e.Time));
        samples.FindChannel("value")!.ReadValues().Should().Equal(expected.Select(e => e.Valid ? (object)(double)e.Value : null));
        samples.FindChannel("value")!.ReadDoubles().Select(v => double.IsNaN(v) ? -1 : v).Should().Equal(expected.Select(e => e.Valid ? e.Value : -1.0));
        samples.FindChannel("frame")!.ReadValues().Cast<byte[]>().Select(Convert.ToBase64String).Should().Equal(expected.Select(e => Convert.ToBase64String(e.Frame)));
        samples.FindChannel("text")!.ReadValues().Should().Equal(expected.Select(e => (object)e.Text));

        Mdf4ChannelGroup counter = group.ChannelGroups[2];
        counter.MasterChannel!.ChannelType.Should().Be(Mdf4ChannelType.VirtualMaster);
        counter.ReadTimestamps().Should().Equal(Enumerable.Range(0, virtualCount).Select(i => i * 0.5));
        counter.FindChannel("count")!.ReadDoubles().Should().Equal(Enumerable.Range(0, virtualCount).Select(i => (double)(i + 100)));
    }

    [Fact]
    public void An_Unknown_Record_Id_Is_A_Format_Error()
    {
        byte[] bytes = BuildUnsorted(1, out _, out _);

        // Corrupt the first record id of the DT block.
        int dt = IndexOf(bytes, "##DT");
        bytes[dt + 24] = 0x7F;

        using Mdf4File file = MdfTestFile.Open(bytes);
        Action act = () => file.ChannelGroups.First().CountRecords();
        act.Should().Throw<Mdf4FormatException>().WithMessage("*unknown record id*");
    }

    [Fact]
    public void An_Unfinalized_File_Is_Read_Until_The_Data_Ends()
    {
        // An unfinalized file whose cycle counter was never updated (zero): the reader must not
        // trust it and must stop cleanly at a record cut off in the middle.
        var raw = new RawMdf();
        PendingBlock value = raw.Channel("value", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 16);
        PendingBlock cg = raw.ChannelGroup(0, cycles: 0, dataBytes: 2, invalidationBytes: 0, firstChannel: value);
        PendingBlock dt = raw.DataBlock(new byte[] { 1, 0, 2, 0, 3, 0, 4 });
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, dt), fileId: "UnFinMF ");

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.IsFinalized.Should().BeFalse();
        file.FindChannel("value")!.ReadDoubles().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void A_Finalized_File_Is_Read_Up_To_The_Cycle_Count()
    {
        var raw = new RawMdf();
        PendingBlock value = raw.Channel("value", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock cg = raw.ChannelGroup(0, cycles: 2, dataBytes: 1, invalidationBytes: 0, firstChannel: value);
        PendingBlock dt = raw.DataBlock(new byte[] { 1, 2, 3, 4 });
        byte[] bytes = raw.Build(raw.DataGroup(0, cg, dt));

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.FindChannel("value")!.ReadDoubles().Should().Equal(1, 2);
    }

    internal static byte[] BuildUnsorted(byte recordIdSize, out List<(double Time, byte Value, bool Valid, byte[] Frame, string Text)> expected, out int virtualCount)
    {
        var raw = new RawMdf();

        // Group A (record id 1): t f64 @0, value u8 @8 (invalidation bit 0), len u8 @9,
        // frame max-length bytes @10..13 (length = len), text VLSD offset @14..21 -> group B.
        PendingBlock vlsdGroup = raw.ChannelGroup(2, cycles: 0, dataBytes: 0, invalidationBytes: 0, firstChannel: null, flags: 1);
        PendingBlock text = raw.Channel("text", Mdf4ChannelType.VariableLength, Mdf4DataType.StringUtf8, 14, 64, data: vlsdGroup);
        PendingBlock length = raw.Channel("len", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 9, 8);
        PendingBlock frame = raw.Channel("frame", Mdf4ChannelType.MaximumLength, Mdf4DataType.ByteArray, 10, 32, next: text, data: length);
        length.Links[0] = frame;
        PendingBlock value = raw.Channel("value", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 8, 8, next: length, flags: Mdf4ChannelFlags.InvalidationBitValid, invalidationBit: 0);
        PendingBlock time = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.FloatLe, 0, 64, next: value, sync: Mdf4SyncType.Time);

        // Group C (record id 3): a virtual time master (index * 0.5) and a u8 counter @0.
        PendingBlock count = raw.Channel("count", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock virtualTime = raw.Channel("vt", Mdf4ChannelType.VirtualMaster, Mdf4DataType.UnsignedIntegerLe, 0, 0, next: count, sync: Mdf4SyncType.Time, conversion: raw.Linear(0.5, 0));

        expected = new List<(double, byte, bool, byte[], string)>();
        var stream = new MemoryStream();
        long vlsdOffset = 0;
        long vlsdTotal = 0;
        int samples = 0, counters = 0;
        for (int i = 0; i < 60; i++)
        {
            if (i % 3 != 2)
            {
                string message = i % 5 == 0 ? string.Empty : "msg " + i;
                byte[] utf8 = Encoding.UTF8.GetBytes(message);
                WriteId(stream, recordIdSize, 2);
                WriteU32(stream, (uint)utf8.Length);
                stream.Write(utf8, 0, utf8.Length);

                var record = new byte[23];
                double t = samples * 0.25;
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0), BitConverter.DoubleToInt64Bits(t));
                record[8] = (byte)(i * 3);
                int frameLength = i % 5;
                record[9] = (byte)frameLength;
                for (int k = 0; k < 4; k++)
                {
                    record[10 + k] = (byte)(0xA0 + k + i);
                }

                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(14), (ulong)vlsdOffset);
                bool valid = i % 4 != 0;
                record[22] = (byte)(valid ? 0 : 1);
                WriteId(stream, recordIdSize, 1);
                stream.Write(record, 0, record.Length);

                expected.Add((t, record[8], valid, record.AsSpan(10, frameLength).ToArray(), message));
                vlsdOffset += 4 + utf8.Length;
                vlsdTotal += 4 + utf8.Length;
                samples++;
            }
            else
            {
                WriteId(stream, recordIdSize, 3);
                stream.WriteByte((byte)(100 + counters));
                counters++;
            }
        }

        virtualCount = counters;
        BinaryPrimitives.WriteUInt32LittleEndian(vlsdGroup.Data.AsSpan(24), (uint)vlsdTotal);
        PendingBlock counterGroup = raw.ChannelGroup(3, cycles: (ulong)counters, dataBytes: 1, invalidationBytes: 0, firstChannel: virtualTime, name: "Counter");
        vlsdGroup.Links[0] = counterGroup;
        PendingBlock sampleGroup = raw.ChannelGroup(1, cycles: (ulong)samples, dataBytes: 22, invalidationBytes: 1, firstChannel: time, name: "Samples", next: vlsdGroup);
        PendingBlock data = raw.DataBlock(stream.ToArray());
        return raw.Build(raw.DataGroup(recordIdSize, sampleGroup, data));
    }

    private static void WriteId(Stream stream, byte size, ulong id)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
        stream.Write(bytes, 0, size);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes, 0, 4);
    }

    private static int IndexOf(byte[] haystack, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= haystack.Length - n.Length; i++)
        {
            if (haystack.AsSpan(i, n.Length).SequenceEqual(n))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"{needle} not found");
    }
}
