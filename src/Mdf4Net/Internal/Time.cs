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

        long maxNs = (DateTime.MaxValue.Ticks - UnixEpochTicks) * 100;
        long ticks = UnixEpochTicks + (long)(Math.Min(ns, (ulong)maxNs) / 100);
        var utc = new DateTimeOffset(ticks, TimeSpan.Zero);

        // Bit 1: the offsets are valid. Bit 0: the time stamp is local time, not UTC.
        bool offsetsValid = (flags & 0x02) != 0;
        if (offsetsValid && Math.Abs(tz + dst) < 14 * 60)
        {
            return utc.ToOffset(TimeSpan.FromMinutes(tz + dst));
        }

        return utc;
    }

    public static ulong ToNanoseconds(DateTimeOffset time)
    {
        long ticks = time.UtcTicks - UnixEpochTicks;
        return ticks <= 0 ? 0 : (ulong)ticks * 100;
    }
}
