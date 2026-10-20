using System;

namespace Mdf4Net.Internal;

/// <summary>Conversion between MDF time stamps (ns since 1970-01-01 UTC) and .NET time.</summary>
internal static class Time
{
    private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>
    /// Reads the common time fields of HD and FH: <c>time_ns</c> (u64), <c>tz_offset_min</c>,
    /// <c>dst_offset_min</c> (i16 each) and <c>time_flags</c> (u8).
    /// </summary>
    public static DateTimeOffset FromBlock(RawBlock block, int offset)
    {
        ulong ns = block.U64(offset);
        short tz = block.I16(offset + 8);
        short dst = block.I16(offset + 10);
        byte flags = block.U8(offset + 12);

        // Keep a day of margin at both ends so that applying an offset cannot leave DateTime's range.
        long maxNs = (DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay - UnixEpochTicks) * 100;
        long ticks = UnixEpochTicks + (long)(Math.Min(ns, (ulong)maxNs) / 100);

        // Bit 0: the time stamp is the local wall clock, not UTC. Bit 1: the offsets are valid.
        bool localTime = (flags & 0x01) != 0;
        bool offsetsValid = (flags & 0x02) != 0 && Math.Abs(tz + dst) < 14 * 60;
        TimeSpan zone = offsetsValid ? TimeSpan.FromMinutes(tz + dst) : TimeSpan.Zero;

        if (localTime)
        {
            // A wall-clock reading: with valid offsets it names an instant in that zone; without,
            // the zone is unknown and the reading is returned as it is, with a zero offset.
            return new DateTimeOffset(ticks, zone);
        }

        return new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(zone);
    }

    public static ulong ToNanoseconds(DateTimeOffset time)
    {
        long ticks = time.UtcTicks - UnixEpochTicks;
        return ticks <= 0 ? 0 : (ulong)ticks * 100;
    }
}
