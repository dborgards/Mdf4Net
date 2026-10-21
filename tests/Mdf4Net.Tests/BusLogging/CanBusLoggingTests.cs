using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Mdf4Net.BusLogging;
using Mdf4Net.Internal;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests.BusLogging;

public class CanBusLoggingTests
{
    [Theory]
    [InlineData(Mdf4Compression.None)]
    [InlineData(Mdf4Compression.TransposedDeflate)]
    public void Frames_Round_Trip(Mdf4Compression compression)
    {
        var random = new Random(7);
        var frames = new List<CanDataFrame>();
        int[] fdLengths = { 0, 3, 8, 12, 16, 20, 24, 32, 48, 64 };
        for (int i = 0; i < 500; i++)
        {
            bool fd = i % 2 == 0;
            bool extended = i % 3 == 0;
            var data = new byte[fd ? fdLengths[i % fdLengths.Length] : i % 9];
            random.NextBytes(data);
            CanFrameFlags flags = (extended ? CanFrameFlags.Extended : 0)
                | (fd ? CanFrameFlags.FlexibleDataRate : 0)
                | (fd && i % 4 == 0 ? CanFrameFlags.BitRateSwitch : 0)
                | (fd && i % 8 == 0 ? CanFrameFlags.ErrorStateIndicator : 0);
            uint id = extended ? (uint)random.Next(0x800, 0x1FFFFFFF) : (uint)random.Next(0, 0x800);
            frames.Add(new CanDataFrame(i * 0.001, id, data, flags, (byte)(i % 3), i % 5 == 0 ? CanDirection.Transmit : CanDirection.Receive));
        }

        byte[] bytes = MdfTestFile.Write(
            writer =>
            {
                CanDataFrameWriter can = writer.AddCanDataFrameGroup(new CanBusLoggingOptions { NetworkName = "CAN1" });
                foreach (CanDataFrame frame in frames)
                {
                    can.Write(frame);
                }

                can.FrameCount.Should().Be(frames.Count);
            },
            new Mdf4WriterOptions { Compression = compression, DataBlockSize = 4096 });

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4ChannelGroup group = file.ChannelGroups.Single();
        group.IsCanDataFrameGroup().Should().BeTrue();
        group.Name.Should().Be("CAN_DataFrame");
        group.Source!.BusType.Should().Be(Mdf4BusType.Can);
        group.Source.Name.Should().Be("CAN1");
        group.Flags.Should().HaveFlag(Mdf4ChannelGroupFlags.BusEvent);
        group.ReadCanDataFrames().Should().Equal(frames);
        file.ReadCanDataFrames().Should().Equal(frames);

        // The generic channel API reads the same group the same way.
        group.ReadTimestamps().Should().Equal(frames.Select(f => f.Timestamp));
        group.FindChannel("CAN_DataFrame.ID")!.ReadDoubles().Should().Equal(frames.Select(f => (double)f.Id));
        group.FindChannel("CAN_DataFrame.DataLength")!.ReadDoubles().Should().Equal(frames.Select(f => (double)f.Data.Length));
    }

    [Fact]
    public void A_Bus_Logging_Group_With_A_Remote_Master_Takes_Its_Time_Stamps_From_That_Group_On_Both_Read_Paths()
    {
        // A CAN_DataFrame group without its own time channel, taking it from another group
        // (MDF 4.20 remote master). The second master sample is invalid: that frame has no time
        // stamp and is not a frame, just as with an invalid master sample of the group's own.
        var raw = new Mdf4Net.Tests.Infrastructure.RawMdf();
        (PendingBlock master, PendingBlock dgMaster) = RemoteMasterReaderTests.MasterGroup(raw, invalidation: true);
        byte[] bytes = raw.Build(RemoteCanGroup(raw, master, dgMaster), version: 420);

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4ChannelGroup group = file.ChannelGroups.First();
        group.IsCanDataFrameGroup().Should().BeTrue();
        group.RemoteMasterGroup.Should().BeSameAs(file.ChannelGroups.Last());

        double[] timestamps = group.ReadTimestamps();
        timestamps.Should().HaveCount(3);
        timestamps[0].Should().Be(0.5);
        double.IsNaN(timestamps[1]).Should().BeTrue();
        timestamps[2].Should().Be(1.0);

        var expected = new[]
        {
            new CanDataFrame(0.5, 0x100, new byte[] { 0xAA, 0, 0, 0, 0, 0, 0, 0 }, busChannel: 0),
            new CanDataFrame(1.0, 0x102, new byte[] { 0xBB, 0, 0, 0, 0, 0, 0, 0 }, busChannel: 0),
        };
        group.ReadCanDataFrames().Should().Equal(expected);
        file.ReadCanDataFrames().Should().Equal(expected);
    }

    [Fact]
    public void A_Remote_Master_Sample_That_Is_NaN_But_Valid_Keeps_Its_Frame()
    {
        // Only an invalidated master sample drops a frame; a stored NaN is a time stamp of NaN,
        // as it is for a group's own master.
        var raw = new Mdf4Net.Tests.Infrastructure.RawMdf();
        PendingBlock time = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.FloatLe, 0, 32, sync: Mdf4SyncType.Time);
        PendingBlock master = raw.ChannelGroup(0, 3, 4, 0, time, name: "master");
        var records = new byte[12];
        BitConverter.GetBytes(0.5f).CopyTo(records, 0);
        BitConverter.GetBytes(float.NaN).CopyTo(records, 4);
        BitConverter.GetBytes(1.0f).CopyTo(records, 8);
        PendingBlock dgMaster = raw.DataGroup(0, master, raw.DataBlock(records));
        byte[] bytes = raw.Build(RemoteCanGroup(raw, master, dgMaster), version: 420);

        using Mdf4File file = MdfTestFile.Open(bytes);
        List<CanDataFrame> frames = file.ChannelGroups.First().ReadCanDataFrames().ToList();
        frames.Select(f => f.Id).Should().Equal(0x100u, 0x101u, 0x102u);
        frames[0].Timestamp.Should().Be(0.5);
        double.IsNaN(frames[1].Timestamp).Should().BeTrue();
        frames[2].Timestamp.Should().Be(1.0);
    }

    /// <summary>
    /// A sorted data group in front of <paramref name="masterDataGroup"/> holding a CAN_DataFrame
    /// group with the remote master <paramref name="masterGroup"/> and three frames: IDs 0x100,
    /// 0x101 and 0x102 with the payloads AA 00…, 00…, BB 00….
    /// </summary>
    private static PendingBlock RemoteCanGroup(Mdf4Net.Tests.Infrastructure.RawMdf raw, PendingBlock masterGroup, PendingBlock masterDataGroup)
    {
        PendingBlock dataBytes = raw.Channel("CAN_DataFrame.DataBytes", Mdf4ChannelType.FixedLength, Mdf4DataType.ByteArray, 4, 64, flags: Mdf4ChannelFlags.BusEvent);
        PendingBlock id = raw.Channel("CAN_DataFrame.ID", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 29, next: dataBytes, flags: Mdf4ChannelFlags.BusEvent);
        PendingBlock frame = raw.Channel("CAN_DataFrame", Mdf4ChannelType.FixedLength, Mdf4DataType.ByteArray, 0, 96, flags: Mdf4ChannelFlags.BusEvent, composition: id);
        PendingBlock cg = raw.ChannelGroup(0, 3, 12, 0, frame, flags: (ushort)(Mdf4ChannelGroupFlags.BusEvent | Mdf4ChannelGroupFlags.RemoteMaster), masterGroup: masterGroup);
        var records = new byte[36];
        records[0] = 0x00;
        records[1] = 0x01; // ID 0x100
        records[4] = 0xAA;
        records[12] = 0x01;
        records[13] = 0x01; // ID 0x101
        records[24] = 0x02;
        records[25] = 0x01; // ID 0x102
        records[28] = 0xBB;
        return raw.DataGroup(0, cg, raw.DataBlock(records), next: masterDataGroup);
    }

    [Fact]
    public void The_Layout_Follows_The_Asam_Bus_Logging_Names()
    {
        byte[] bytes = MdfTestFile.Write(writer => writer.AddCanDataFrameGroup().Write(0.5, 0x123, new byte[] { 1, 2 }));

        using Mdf4File file = MdfTestFile.Open(bytes);
        Mdf4Channel frame = file.ChannelGroups.Single().Channels.Single(c => c.Name == "CAN_DataFrame");
        frame.DataType.Should().Be(Mdf4DataType.ByteArray);
        frame.Flags.Should().HaveFlag(Mdf4ChannelFlags.BusEvent);
        frame.Children.Select(c => c.Name.Substring("CAN_DataFrame.".Length)).Should().Equal(
            "BusChannel", "ID", "IDE", "DLC", "Dir", "EDL", "BRS", "DataLength", "ESI", "DataBytes");
        file.FindChannel("CAN_DataFrame.ID")!.ReadDoubles().Should().Equal(0x123);
        file.FindChannel("CAN_DataFrame.DLC")!.ReadDoubles().Should().Equal(2);
        file.FindChannel("CAN_DataFrame.DataLength")!.ReadDoubles().Should().Equal(2);
        file.FindChannel("t")!.ReadDoubles().Should().Equal(0.5);
    }

    [Fact]
    public void Classic_Groups_Refuse_Fd_Payloads()
    {
        using var stream = new System.IO.MemoryStream();
        using Mdf4Writer writer = Mdf4Writer.Create(stream, leaveOpen: true);
        CanDataFrameWriter can = writer.AddCanDataFrameGroup(new CanBusLoggingOptions { MaxDataLength = 8 });

        Action write = () => can.Write(0, 1, new byte[12], CanFrameFlags.FlexibleDataRate);
        write.Should().Throw<ArgumentException>();
        ((Action)(() => writer.AddCanDataFrameGroup(new CanBusLoggingOptions { MaxDataLength = 16 }))).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0x800u, CanFrameFlags.None, 0)]
    [InlineData(0x20000000u, CanFrameFlags.Extended, 0)]
    [InlineData(1u, CanFrameFlags.None, 9)]
    [InlineData(1u, CanFrameFlags.FlexibleDataRate, 13)]
    [InlineData(1u, CanFrameFlags.BitRateSwitch, 0)]
    public void Invalid_Frames_Are_Rejected(uint id, CanFrameFlags flags, int length)
    {
        Action create = () => _ = new CanDataFrame(0, id, new byte[length], flags);

        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(8, false, 8)]
    [InlineData(9, false, 8)]
    [InlineData(9, true, 12)]
    [InlineData(13, true, 32)]
    [InlineData(15, true, 64)]
    public void Data_Length_Codes_Map_As_Iso_11898_Defines(int dlc, bool fd, int length)
    {
        CanDataFrame.DlcToLength(dlc, fd).Should().Be(length);
        CanDataFrame.LengthToDlc(length, fd).Should().Be(fd ? dlc : Math.Min(dlc, 8));
    }

    [Fact]
    public void Undefined_Flag_Bits_And_Directions_Are_Refused()
    {
        ((Action)(() => _ = new CanDataFrame(0, 1, new byte[1], (CanFrameFlags)0x80))).Should().Throw<ArgumentException>().WithMessage("*not defined*");
        ((Action)(() => _ = new CanDataFrame(0, 1, new byte[1], CanFrameFlags.None, 1, (CanDirection)2))).Should().Throw<ArgumentException>().WithMessage("*direction*");
    }

    [Fact]
    public void Frames_Compare_By_Value()
    {
        var a = new CanDataFrame(1, 2, new byte[] { 3 });
        var b = new CanDataFrame(1, 2, new byte[] { 3 });

        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a != new CanDataFrame(1, 2, new byte[] { 4 })).Should().BeTrue();
        a.ToString().Should().Contain("002").And.Contain("03");
    }

    [Fact]
    public void A_Group_That_Is_Not_Bus_Logging_Is_Refused()
    {
        byte[] bytes = MdfTestFile.Write(writer =>
        {
            var definition = new Mdf4ChannelGroupDefinition("Plain");
            definition.Add(Mdf4ChannelDefinition.UInt8("CAN_DataFrame.ID"));
            writer.AddChannelGroup(definition);
        });

        using Mdf4File file = MdfTestFile.Open(bytes);
        file.ChannelGroups.Single().IsCanDataFrameGroup().Should().BeFalse();
        file.ReadCanDataFrames().Should().BeEmpty();
        ((Action)(() => file.ChannelGroups.Single().ReadCanDataFrames().ToList())).Should().Throw<ArgumentException>();
    }
}
