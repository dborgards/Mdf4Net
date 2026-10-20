using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Mdf4Net.BusLogging;
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
