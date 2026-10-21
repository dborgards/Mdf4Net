using System;
using System.Linq;
using AwesomeAssertions;
using Mdf4Net.Internal;
using Mdf4Net.Tests.Infrastructure;
using Xunit;

namespace Mdf4Net.Tests;

/// <summary>
/// Channel groups that take their master channel from another group (<c>cg_flags</c> bit 3 and
/// <c>cg_cg_master</c>, MDF 4.20). No writer in reach produces them with record-oriented data,
/// so the files are laid out block by block; eng/interop/check_asammdf.py crafts the same layout
/// and checks asammdf's reading of it.
/// </summary>
public class RemoteMasterReaderTests
{
    /// <summary>
    /// A sorted data group holding the group "master": a time channel with three records, raw
    /// 2, 3, 4 at 0.25 s, so 0.5, 0.75 and 1.0 s. With <paramref name="invalidation"/> the
    /// record carries an invalidation byte and the second sample is marked invalid.
    /// </summary>
    internal static (PendingBlock Group, PendingBlock DataGroup) MasterGroup(RawMdf raw, bool invalidation = false)
    {
        PendingBlock time = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.UnsignedIntegerLe, 0, 8, sync: Mdf4SyncType.Time, conversion: raw.Linear(0.25, 0),
            flags: invalidation ? Mdf4ChannelFlags.InvalidationBitValid : Mdf4ChannelFlags.None, invalidationBit: 0);
        PendingBlock group = raw.ChannelGroup(0, 3, 1, invalidation ? 1u : 0u, time, name: "master");
        byte[] records = invalidation ? new byte[] { 2, 0, 3, 1, 4, 0 } : new byte[] { 2, 3, 4 };
        return (group, raw.DataGroup(0, group, raw.DataBlock(records)));
    }

    /// <summary>
    /// Two sorted data groups. The first holds the group "remote" (channel <c>v</c>, three
    /// records 10, 20, 30) which takes its master from the group of <see cref="MasterGroup"/> in
    /// the second data group, so the master group comes later in the data group chain than the
    /// group referring to it.
    /// </summary>
    public static byte[] BuildRemoteMaster()
    {
        var raw = new RawMdf();
        (PendingBlock masterGroup, PendingBlock masterData) = MasterGroup(raw);
        PendingBlock value = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock remoteGroup = raw.ChannelGroup(0, 3, 1, 0, value, name: "remote", flags: (ushort)Mdf4ChannelGroupFlags.RemoteMaster, masterGroup: masterGroup);
        PendingBlock remoteData = raw.DataGroup(0, remoteGroup, raw.DataBlock(new byte[] { 10, 20, 30 }), next: masterData);
        return raw.Build(remoteData, version: 420);
    }

    [Fact]
    public void The_Time_Stamps_Come_From_The_Master_Group_In_Another_Data_Group()
    {
        using Mdf4File file = MdfTestFile.Open(BuildRemoteMaster());
        file.DataGroups.Should().HaveCount(2);
        Mdf4ChannelGroup remote = file.DataGroups[0].ChannelGroups.Single();
        Mdf4ChannelGroup master = file.DataGroups[1].ChannelGroups.Single();

        remote.Name.Should().Be("remote");
        remote.Flags.Should().HaveFlag(Mdf4ChannelGroupFlags.RemoteMaster);
        remote.MasterChannel.Should().BeNull("the group's own master channel is what MasterChannel reports");
        remote.RemoteMasterGroup.Should().BeSameAs(master);
        remote.ResolvedMasterChannel.Should().BeSameAs(master.MasterChannel);
        master.RemoteMasterGroup.Should().BeNull();
        master.ResolvedMasterChannel.Should().BeSameAs(master.MasterChannel);

        remote.ReadTimestamps().Should().Equal(0.5, 0.75, 1.0);
        remote.FindChannel("v")!.ReadDoubles().Should().Equal(10, 20, 30);
        master.ReadTimestamps().Should().Equal(0.5, 0.75, 1.0);
    }

    [Fact]
    public void A_Chain_Of_Remote_Masters_Ends_At_The_Group_With_The_Master_Channel()
    {
        var raw = new RawMdf();
        PendingBlock time = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.UnsignedIntegerLe, 0, 8, sync: Mdf4SyncType.Time, conversion: raw.Linear(0.25, 0));
        PendingBlock c = raw.ChannelGroup(0, 3, 1, 0, time, name: "c");
        PendingBlock b = raw.ChannelGroup(0, 3, 1, 0, raw.Channel("b", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8), name: "b", flags: (ushort)Mdf4ChannelGroupFlags.RemoteMaster, masterGroup: c);
        PendingBlock a = raw.ChannelGroup(0, 3, 1, 0, raw.Channel("a", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8), name: "a", flags: (ushort)Mdf4ChannelGroupFlags.RemoteMaster, masterGroup: b);
        PendingBlock dgC = raw.DataGroup(0, c, raw.DataBlock(new byte[] { 2, 3, 4 }));
        PendingBlock dgB = raw.DataGroup(0, b, raw.DataBlock(new byte[] { 4, 5, 6 }), next: dgC);
        PendingBlock dgA = raw.DataGroup(0, a, raw.DataBlock(new byte[] { 7, 8, 9 }), next: dgB);

        using Mdf4File file = MdfTestFile.Open(raw.Build(dgA, version: 420));
        Mdf4ChannelGroup[] groups = file.ChannelGroups.ToArray();
        groups.Select(g => g.Name).Should().Equal("a", "b", "c");
        groups[0].RemoteMasterGroup.Should().BeSameAs(groups[2]);
        groups[1].RemoteMasterGroup.Should().BeSameAs(groups[2]);
        groups[0].ReadTimestamps().Should().Equal(0.5, 0.75, 1.0);
        groups[1].ReadTimestamps().Should().Equal(0.5, 0.75, 1.0);
    }

    [Fact]
    public void The_Master_Group_May_Share_An_Unsorted_Data_Group()
    {
        var raw = new RawMdf();
        PendingBlock time = raw.Channel("t", Mdf4ChannelType.Master, Mdf4DataType.UnsignedIntegerLe, 0, 8, sync: Mdf4SyncType.Time, conversion: raw.Linear(0.25, 0));
        PendingBlock master = raw.ChannelGroup(1, 3, 1, 0, time, name: "master");
        PendingBlock value = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock remote = raw.ChannelGroup(2, 3, 1, 0, value, name: "remote", flags: (ushort)Mdf4ChannelGroupFlags.RemoteMaster, masterGroup: master, next: master);
        byte[] records = { 2, 10, 1, 2, 2, 20, 1, 3, 1, 4, 2, 30 };

        using Mdf4File file = MdfTestFile.Open(raw.Build(raw.DataGroup(1, remote, raw.DataBlock(records)), version: 420));
        Mdf4ChannelGroup group = file.ChannelGroups.First();
        group.Name.Should().Be("remote");
        group.RemoteMasterGroup.Should().BeSameAs(file.ChannelGroups.Last());
        group.ReadTimestamps().Should().Equal(0.5, 0.75, 1.0);
        group.FindChannel("v")!.ReadDoubles().Should().Equal(10, 20, 30);
    }

    [Fact]
    public void An_Invalid_Master_Sample_Reads_As_NaN_For_The_Remote_Group()
    {
        var raw = new RawMdf();
        (PendingBlock master, PendingBlock dgMaster) = MasterGroup(raw, invalidation: true);
        PendingBlock value = raw.Channel("v", Mdf4ChannelType.FixedLength, Mdf4DataType.UnsignedIntegerLe, 0, 8);
        PendingBlock remote = raw.ChannelGroup(0, 3, 1, 0, value, name: "remote", flags: (ushort)Mdf4ChannelGroupFlags.RemoteMaster, masterGroup: master);
        PendingBlock dgRemote = raw.DataGroup(0, remote, raw.DataBlock(new byte[] { 10, 20, 30 }), next: dgMaster);

        using Mdf4File file = MdfTestFile.Open(raw.Build(dgRemote, version: 420));
        double[] timestamps = file.ChannelGroups.First().ReadTimestamps();
        timestamps.Should().HaveCount(3);
        timestamps[0].Should().Be(0.5);
        double.IsNaN(timestamps[1]).Should().BeTrue();
        timestamps[2].Should().Be(1.0);
    }
}
