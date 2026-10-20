# Mdf4Net

Read and write **ASAM MDF 4.x** (`.mf4`) measurement data files in pure .NET — no native
dependencies, `netstandard2.0` and `net10.0`.

- **Writer:** sorted data groups, DT/DL blocks, DZ compression (deflate or transposed deflate),
  variable-length strings and byte arrays (VLSD), conversions, sources, MDF 4.10 or 4.20.
- **Reader:** sorted and unsorted data groups, DT/DL/DZ/HL, SD and VLSD channel groups,
  invalidation bits, bit fields in both byte orders, conversions (linear, rational, tables,
  value/range to text, text to value), compositions, unfinalized files.
- **CAN bus logging:** `CAN_DataFrame` groups for CAN and CAN FD as asammdf, CANape and CANoe
  expect them.
- **Hardened reader:** malformed files raise `Mdf4FormatException`; allocation is bounded by
  `Mdf4ReaderOptions`.

## Write

```csharp
using Mdf4Net;

using var writer = Mdf4Writer.Create("drive.mf4", new Mdf4WriterOptions
{
    Compression = Mdf4Compression.TransposedDeflate,
});

var engine = new Mdf4ChannelGroupDefinition("Engine");
engine.AddTime();
var rpm = engine.Add(new Mdf4ChannelDefinition("Rpm", Mdf4DataType.UnsignedIntegerLe, 16)
{
    Unit = "1/min",
    Conversion = Mdf4ConversionDefinition.Linear(factor: 0.25),
});
var temperature = engine.Add(Mdf4ChannelDefinition.Float32("CoolantTemp"));

Mdf4ChannelGroupWriter group = writer.AddChannelGroup(engine);
group.BeginRecord(0.010).SetPhysical(rpm, 2400).Set(temperature, 87.5).Commit();
```

## Read

```csharp
using var file = Mdf4File.Open("drive.mf4");
Mdf4Channel rpm = file.FindChannel("Rpm")!;
double[] time = rpm.ChannelGroup.ReadTimestamps();
double[] values = rpm.ReadDoubles();   // physical values, NaN where invalid
```

## CAN bus logging

```csharp
using Mdf4Net.BusLogging;

CanDataFrameWriter can = writer.AddCanDataFrameGroup(new CanBusLoggingOptions { NetworkName = "CAN1" });
can.Write(0.0012, 0x181, pdoPayload);
can.Write(0.0015, 0x18FF0102, fdPayload, CanFrameFlags.Extended | CanFrameFlags.FlexibleDataRate);

IReadOnlyList<CanDataFrame> frames = Mdf4File.Open("can.mf4").ReadCanDataFrames();
```

Documentation, format coverage and the interoperability check against asammdf:
<https://github.com/dborgards/Mdf4Net>
