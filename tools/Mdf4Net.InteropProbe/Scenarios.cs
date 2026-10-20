using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mdf4Net.BusLogging;

namespace Mdf4Net.InteropProbe;

internal sealed record Scenario(string Name, Func<string, Expectation> Write);

/// <summary>
/// The files written for the interoperability check. Each covers a writer feature another reader
/// has to understand: data types and bit layouts, conversions, block chaining, both compression
/// modes, variable-length signal data, several groups, MDF 4.20, and CAN bus logging.
/// </summary>
internal static class Scenarios
{
    public static IReadOnlyList<Scenario> All { get; } = new[]
    {
        new Scenario("types_plain", path => Types(path, Mdf4Compression.None, 4 * 1024 * 1024, Mdf4Version.V410)),
        new Scenario("types_multiblock", path => Types(path, Mdf4Compression.None, 1024, Mdf4Version.V410)),
        new Scenario("types_deflate", path => Types(path, Mdf4Compression.Deflate, 4096, Mdf4Version.V410)),
        new Scenario("types_transposed", path => Types(path, Mdf4Compression.TransposedDeflate, 4096, Mdf4Version.V410)),
        new Scenario("types_v420", path => Types(path, Mdf4Compression.TransposedDeflate, 4096, Mdf4Version.V420)),
        new Scenario("bitfields", BitFields),
        new Scenario("vlsd", path => VariableLength(path, Mdf4Compression.None)),
        new Scenario("vlsd_deflate", path => VariableLength(path, Mdf4Compression.Deflate)),
        new Scenario("multigroup", MultiGroup),
        new Scenario("can", path => Can(path, Mdf4Compression.None)),
        new Scenario("can_transposed", path => Can(path, Mdf4Compression.TransposedDeflate)),
        new Scenario("can_pdo", CanPdo),
    };

    /// <summary>
    /// A CANopen TPDO1 of node 1 (COB-ID 0x181): bytes 0..1 an unsigned speed (factor 0.5 rpm),
    /// byte 2 a counter. check_asammdf.py decodes it with a matching DBC through asammdf's
    /// extract_bus_logging, the path a CAN bus log normally takes to physical signals.
    /// </summary>
    private static Expectation CanPdo(string path)
    {
        var expectation = new Expectation();
        using Mdf4Writer writer = Mdf4Writer.Create(path, new Mdf4WriterOptions { Compression = Mdf4Compression.TransposedDeflate });
        CanDataFrameWriter can = writer.AddCanDataFrameGroup(new CanBusLoggingOptions { NetworkName = "CAN1", MaxDataLength = 8 });
        for (int i = 0; i < 400; i++)
        {
            ushort speed = (ushort)(i * 7);
            var frame = new CanDataFrame(i * 0.01, 0x181, new byte[] { (byte)speed, (byte)(speed >> 8), (byte)i }, busChannel: 1);
            can.Write(frame);
            expectation.Frames.Add(frame);
            if (i % 50 == 0)
            {
                // Another node's PDO in between, which the DBC does not describe.
                var other = new CanDataFrame((i * 0.01) + 0.005, 0x282, new byte[] { 1, 2 }, busChannel: 1);
                can.Write(other);
                expectation.Frames.Add(other);
            }
        }

        return expectation;
    }

    private static Expectation Types(string path, Mdf4Compression compression, int blockSize, Mdf4Version version)
    {
        var expectation = new Expectation();
        var recorder = new Recorder(expectation, 0);
        using Mdf4Writer writer = Mdf4Writer.Create(path, new Mdf4WriterOptions
        {
            Compression = compression,
            DataBlockSize = blockSize,
            Version = version,
            Comment = "interop: data types",
        });

        var definition = new Mdf4ChannelGroupDefinition("Types");
        definition.AddTime();
        Mdf4ChannelDefinition u8 = definition.Add(Mdf4ChannelDefinition.UInt8("u8"));
        Mdf4ChannelDefinition u16 = definition.Add(new Mdf4ChannelDefinition("u16_linear", Mdf4DataType.UnsignedIntegerLe, 16)
        {
            Unit = "km/h",
            Conversion = Mdf4ConversionDefinition.Linear(0.25, -10),
        });
        Mdf4ChannelDefinition u32 = definition.Add(Mdf4ChannelDefinition.UInt32("u32"));
        Mdf4ChannelDefinition u64 = definition.Add(Mdf4ChannelDefinition.UInt64("u64"));
        Mdf4ChannelDefinition i8 = definition.Add(Mdf4ChannelDefinition.Int8("i8"));
        Mdf4ChannelDefinition i16 = definition.Add(Mdf4ChannelDefinition.Int16("i16"));
        Mdf4ChannelDefinition i32 = definition.Add(Mdf4ChannelDefinition.Int32("i32"));
        Mdf4ChannelDefinition i64 = definition.Add(Mdf4ChannelDefinition.Int64("i64"));
        Mdf4ChannelDefinition f32 = definition.Add(Mdf4ChannelDefinition.Float32("f32"));
        Mdf4ChannelDefinition f64 = definition.Add(new Mdf4ChannelDefinition("f64", Mdf4DataType.FloatLe, 64) { Unit = "bar" });
        Mdf4ChannelDefinition beU16 = definition.Add(new Mdf4ChannelDefinition("u16_be", Mdf4DataType.UnsignedIntegerBe, 16));
        Mdf4ChannelDefinition beI32 = definition.Add(new Mdf4ChannelDefinition("i32_be", Mdf4DataType.SignedIntegerBe, 32));
        Mdf4ChannelDefinition beF64 = definition.Add(new Mdf4ChannelDefinition("f64_be", Mdf4DataType.FloatBe, 64));
        Mdf4ChannelDefinition rational = definition.Add(new Mdf4ChannelDefinition("u16_rational", Mdf4DataType.UnsignedIntegerLe, 16)
        {
            Conversion = Mdf4ConversionDefinition.Rational(0, 2, 1, 0, 0, 4),
        });
        Mdf4ChannelDefinition table = definition.Add(new Mdf4ChannelDefinition("u8_table", Mdf4DataType.UnsignedIntegerLe, 8)
        {
            Conversion = Mdf4ConversionDefinition.ValueToValue(new Dictionary<double, double> { [0] = 0, [100] = 50, [200] = 400 }, interpolate: true),
        });
        Mdf4ChannelDefinition state = definition.Add(new Mdf4ChannelDefinition("state", Mdf4DataType.UnsignedIntegerLe, 8)
        {
            Conversion = Mdf4ConversionDefinition.ValueToText(new Dictionary<double, string> { [0] = "Off", [1] = "Idle", [2] = "Run" }, "Unknown"),
        });
        Mdf4ChannelDefinition floatTable = definition.Add(new Mdf4ChannelDefinition("f64_table", Mdf4DataType.FloatLe, 64)
        {
            Conversion = Mdf4ConversionDefinition.ValueToValue(new Dictionary<double, double> { [0] = 0, [1] = 10 }, interpolate: true),
        });
        Mdf4ChannelDefinition label = definition.Add(Mdf4ChannelDefinition.String("label", 12));
        Mdf4ChannelDefinition raw = definition.Add(Mdf4ChannelDefinition.Bytes("raw", 4));

        Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
        for (int i = 0; i < 3000; i++)
        {
            double time = i * 0.001;
            ushort u16Raw = (ushort)((i * 37) & 0xFFFF);
            ushort rationalRaw = (ushort)(i % 1000);
            byte tableRaw = (byte)(i % 256);
            byte stateRaw = (byte)(i % 4);
            string text = "s" + (i % 97).ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] bytes = { (byte)i, (byte)(i >> 8), 0xA5, (byte)(255 - (i % 256)) };
            long big = (long)i * 1_000_000_007L - 9_000_000_000L;
            ulong u32Raw = (ulong)i * 65537UL;
            ulong u64Raw = (ulong)i * 0x1_0000_0001UL;
            long i16Raw = (i * 11L) - 16000;
            long i32Raw = i * -70001L;
            float f32Raw = i * 0.5f;
            double tableRawFloat = i % 10 == 0 ? double.NaN : (i % 7) * 0.25;

            group.BeginRecord(time)
                .Set(u8, (ulong)(i % 256))
                .Set(u16, (ulong)u16Raw)
                .Set(u32, u32Raw)
                .Set(u64, u64Raw)
                .Set(i8, (long)((i % 256) - 128))
                .Set(i16, i16Raw)
                .Set(i32, i32Raw)
                .Set(i64, big)
                .Set(f32, f32Raw)
                .Set(f64, Math.Sin(i * 0.01) * 1000)
                .Set(beU16, (ulong)u16Raw)
                .Set(beI32, i32Raw)
                .Set(beF64, i * 1.5)
                .Set(rational, (ulong)rationalRaw)
                .Set(table, (ulong)tableRaw)
                .Set(state, (ulong)stateRaw)
                .Set(floatTable, tableRawFloat)
                .Set(label, text)
                .Set(raw, bytes)
                .Commit();

            recorder.Time(time);
            recorder.Add("u8", (double)(i % 256));
            recorder.Add("u16_linear", (u16Raw * 0.25) - 10, "km/h");
            recorder.Add("u32", (double)u32Raw);
            recorder.Add("u64", (double)u64Raw);
            recorder.Add("i8", (double)((i % 256) - 128));
            recorder.Add("i16", (double)i16Raw);
            recorder.Add("i32", (double)i32Raw);
            recorder.Add("i64", (double)big);
            recorder.Add("f32", (double)f32Raw);
            recorder.Add("f64", Math.Sin(i * 0.01) * 1000, "bar");
            recorder.Add("u16_be", (double)u16Raw);
            recorder.Add("i32_be", (double)i32Raw);
            recorder.Add("f64_be", i * 1.5);
            recorder.Add("u16_rational", ((2.0 * rationalRaw) + 1) / 4);
            recorder.Add("u8_table", tableRaw <= 100 ? tableRaw * 0.5 : Math.Min(400, 50 + ((tableRaw - 100) * 3.5)));
            recorder.Add("f64_table", double.IsNaN(tableRawFloat) ? double.NaN : Math.Min(10, tableRawFloat * 10));
            recorder.Add("state", stateRaw switch { 0 => "Off", 1 => "Idle", 2 => "Run", _ => "Unknown" });
            recorder.Add("label", text);
            recorder.Add("raw", bytes);
        }

        return expectation;
    }

    private static Expectation BitFields(string path)
    {
        var expectation = new Expectation();
        var recorder = new Recorder(expectation, 0);
        using Mdf4Writer writer = Mdf4Writer.Create(path);

        var definition = new Mdf4ChannelGroupDefinition("Packed");
        definition.Add(new Mdf4ChannelDefinition("t", Mdf4DataType.FloatLe, 64)
        {
            ChannelType = Mdf4ChannelType.Master,
            SyncType = Mdf4SyncType.Time,
            Unit = "s",
            ByteOffset = 0,
        });
        Mdf4ChannelDefinition flagA = definition.Add(new Mdf4ChannelDefinition("flag_a", Mdf4DataType.UnsignedIntegerLe, 1) { ByteOffset = 8, BitOffset = 0 });
        Mdf4ChannelDefinition flagB = definition.Add(new Mdf4ChannelDefinition("flag_b", Mdf4DataType.UnsignedIntegerLe, 1) { ByteOffset = 8, BitOffset = 1 });
        Mdf4ChannelDefinition nibble = definition.Add(new Mdf4ChannelDefinition("nibble", Mdf4DataType.UnsignedIntegerLe, 4) { ByteOffset = 8, BitOffset = 4 });
        Mdf4ChannelDefinition s12 = definition.Add(new Mdf4ChannelDefinition("signed12", Mdf4DataType.SignedIntegerLe, 12) { ByteOffset = 9, BitOffset = 3 });
        Mdf4ChannelDefinition u20 = definition.Add(new Mdf4ChannelDefinition("u20", Mdf4DataType.UnsignedIntegerLe, 20) { ByteOffset = 11, BitOffset = 2 });

        Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
        for (int i = 0; i < 500; i++)
        {
            double time = i * 0.01;
            long signed = (i * 13 % 4096) - 2048;
            ulong unsigned20 = (ulong)(i * 2099) & 0xFFFFF;
            group.BeginRecord(time)
                .Set(flagA, (i & 1) == 1)
                .Set(flagB, (i & 2) == 2)
                .Set(nibble, (ulong)(i % 16))
                .Set(s12, signed)
                .Set(u20, unsigned20)
                .Commit();

            recorder.Time(time);
            recorder.Add("flag_a", (double)(i & 1));
            recorder.Add("flag_b", (double)((i >> 1) & 1));
            recorder.Add("nibble", (double)(i % 16));
            recorder.Add("signed12", (double)signed);
            recorder.Add("u20", (double)unsigned20);
        }

        return expectation;
    }

    private static Expectation VariableLength(string path, Mdf4Compression compression)
    {
        var expectation = new Expectation();
        var recorder = new Recorder(expectation, 0);
        using Mdf4Writer writer = Mdf4Writer.Create(path, new Mdf4WriterOptions { Compression = compression, DataBlockSize = 2048 });

        var definition = new Mdf4ChannelGroupDefinition("Variable");
        definition.AddTime();
        Mdf4ChannelDefinition message = definition.Add(Mdf4ChannelDefinition.VariableLengthString("message"));
        Mdf4ChannelDefinition blob = definition.Add(Mdf4ChannelDefinition.VariableLengthBytes("blob"));
        Mdf4ChannelDefinition counter = definition.Add(Mdf4ChannelDefinition.UInt16("counter"));

        Mdf4ChannelGroupWriter group = writer.AddChannelGroup(definition);
        for (int i = 0; i < 800; i++)
        {
            double time = i * 0.005;
            string text = i % 50 == 0 ? string.Empty : "Zeile " + i + " äöü " + new string('x', i % 40);
            byte[] bytes = Enumerable.Range(0, (i * 7) % 300).Select(k => (byte)(k ^ i)).ToArray();
            group.BeginRecord(time).Set(message, text).Set(blob, bytes).Set(counter, (ulong)i).Commit();

            recorder.Time(time);
            recorder.Add("message", text);
            recorder.Add("blob", bytes);
            recorder.Add("counter", (double)i);
        }

        return expectation;
    }

    private static Expectation MultiGroup(string path)
    {
        // Year 2500: a start time past the range a signed 64-bit nanosecond count would allow,
        // which MDF stores as unsigned.
        var start = new DateTimeOffset(2500, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var expectation = new Expectation { Start = start };
        using Mdf4Writer writer = Mdf4Writer.Create(path, new Mdf4WriterOptions { DataBlockSize = 4096, StartTime = start });

        var fastDefinition = new Mdf4ChannelGroupDefinition("Fast") { Source = new Mdf4SourceDefinition("ECU1", Mdf4SourceType.Ecu) };
        fastDefinition.AddTime();
        Mdf4ChannelDefinition fastValue = fastDefinition.Add(Mdf4ChannelDefinition.Float64("fast_value"));
        var slowDefinition = new Mdf4ChannelGroupDefinition("Slow");
        slowDefinition.AddTime();
        Mdf4ChannelDefinition slowValue = slowDefinition.Add(Mdf4ChannelDefinition.Int32("slow_value"));
        var eventDefinition = new Mdf4ChannelGroupDefinition("Events");
        eventDefinition.AddTime();
        Mdf4ChannelDefinition eventText = eventDefinition.Add(Mdf4ChannelDefinition.VariableLengthString("event"));

        Mdf4ChannelGroupWriter fast = writer.AddChannelGroup(fastDefinition);
        Mdf4ChannelGroupWriter slow = writer.AddChannelGroup(slowDefinition);
        Mdf4ChannelGroupWriter events = writer.AddChannelGroup(eventDefinition);
        var fastRecorder = new Recorder(expectation, 0);
        var slowRecorder = new Recorder(expectation, 1);
        var eventRecorder = new Recorder(expectation, 2);

        for (int i = 0; i < 2000; i++)
        {
            double time = i * 0.001;
            fast.BeginRecord(time).Set(fastValue, i * 0.1).Commit();
            fastRecorder.Time(time);
            fastRecorder.Add("fast_value", i * 0.1);
            if (i % 100 == 0)
            {
                slow.BeginRecord(time).Set(slowValue, (long)i * -3).Commit();
                slowRecorder.Time(time);
                slowRecorder.Add("slow_value", i * -3.0);
            }

            if (i % 333 == 7)
            {
                string text = "event #" + i;
                events.BeginRecord(time).Set(eventText, text).Commit();
                eventRecorder.Time(time);
                eventRecorder.Add("event", text);
            }
        }

        return expectation;
    }

    private static Expectation Can(string path, Mdf4Compression compression)
    {
        var expectation = new Expectation();
        using Mdf4Writer writer = Mdf4Writer.Create(path, new Mdf4WriterOptions { Compression = compression, DataBlockSize = 8192 });
        CanDataFrameWriter can = writer.AddCanDataFrameGroup(new CanBusLoggingOptions { NetworkName = "CAN1" });

        var random = new Random(4711);
        int[] fdLengths = { 0, 1, 8, 12, 16, 20, 24, 32, 48, 64 };
        for (int i = 0; i < 1500; i++)
        {
            double time = i * 0.0007;
            bool fd = i % 3 == 0;
            bool extended = i % 5 == 0;
            int length = fd ? fdLengths[i % fdLengths.Length] : i % 9;
            var data = new byte[length];
            random.NextBytes(data);
            uint id = extended ? (uint)random.Next(0, 0x1FFFFFFF) : (uint)random.Next(0, 0x800);
            CanFrameFlags flags = (extended ? CanFrameFlags.Extended : 0)
                | (fd ? CanFrameFlags.FlexibleDataRate : 0)
                | (fd && i % 2 == 0 ? CanFrameFlags.BitRateSwitch : 0);
            var frame = new CanDataFrame(time, id, data, flags, (byte)(1 + (i % 2)), i % 7 == 0 ? CanDirection.Transmit : CanDirection.Receive);
            can.Write(frame);
            expectation.Frames.Add(frame);
        }

        return expectation;
    }

    private sealed class Recorder
    {
        private readonly Expectation _expectation;
        private readonly int _group;
        private readonly Dictionary<string, ExpectedChannel> _channels = new();
        private readonly List<double> _timestamps = new();

        public Recorder(Expectation expectation, int group)
        {
            _expectation = expectation;
            _group = group;
        }

        public void Time(double time) => _timestamps.Add(time);

        public void Add(string name, object value, string? unit = null)
        {
            if (!_channels.TryGetValue(name, out ExpectedChannel? channel))
            {
                channel = new ExpectedChannel(_group, name, unit, _timestamps, new List<object?>());
                _channels[name] = channel;
                _expectation.Channels.Add(channel);
            }

            channel.Values.Add(value);
        }
    }
}
