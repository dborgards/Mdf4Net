# Mdf4Net

[![CI](https://github.com/dborgards/Mdf4Net/actions/workflows/ci.yml/badge.svg)](https://github.com/dborgards/Mdf4Net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Mdf4Net.svg)](https://www.nuget.org/packages/Mdf4Net)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Read and write **ASAM MDF 4.x** (`.mf4`) measurement data files in pure C# — no native libraries,
no P/Invoke, one strong-named package for `netstandard2.0` and `net10.0`.

MDF is the standard file format for measurement data in automotive and industrial engineering:
signal recordings from ECUs, data loggers and test benches, and raw bus traffic (CAN, CAN FD, LIN,
…) for later decoding. Mdf4Net lets a .NET application produce files that tools such as asammdf,
CANape, CANoe or MATLAB open directly, and read the files those tools and loggers produce.

```
dotnet add package Mdf4Net
```

## Quick start

### Write signals

```csharp
using Mdf4Net;

using var writer = Mdf4Writer.Create("drive.mf4", new Mdf4WriterOptions
{
    Compression = Mdf4Compression.TransposedDeflate,   // DZ blocks
    Comment = "Test drive 42",
});

var engine = new Mdf4ChannelGroupDefinition("Engine");
engine.AddTime();                                       // master channel "t", seconds
var rpm = engine.Add(new Mdf4ChannelDefinition("Rpm", Mdf4DataType.UnsignedIntegerLe, 16)
{
    Unit = "1/min",
    Conversion = Mdf4ConversionDefinition.Linear(factor: 0.25),
});
var coolant = engine.Add(Mdf4ChannelDefinition.Float32("CoolantTemp"));
var state = engine.Add(new Mdf4ChannelDefinition("State", Mdf4DataType.UnsignedIntegerLe, 8)
{
    Conversion = Mdf4ConversionDefinition.ValueToText(
        new Dictionary<double, string> { [0] = "Off", [1] = "Idle", [2] = "Run" }),
});

Mdf4ChannelGroupWriter group = writer.AddChannelGroup(engine);
group.BeginRecord(0.010)
    .SetPhysical(rpm, 2400)       // inverts the linear conversion: raw 9600
    .Set(coolant, 87.5)
    .Set(state, 2L)
    .Commit();
```

Records are buffered per channel group and written in blocks as they fill, so a recording of any
length needs bounded memory. The file is finalized when the writer is disposed.

### Read signals

```csharp
using var file = Mdf4File.Open("drive.mf4");

Mdf4Channel rpm = file.FindChannel("Rpm")!;
double[] time = rpm.ChannelGroup.ReadTimestamps();
double[] values = rpm.ReadDoubles();            // physical values; NaN where invalid
object?[] states = file.FindChannel("State")!.ReadValues();   // "Off", "Idle", "Run", …

foreach (Mdf4ChannelGroup cg in file.ChannelGroups)
{
    Console.WriteLine($"{cg.Name}: {string.Join(", ", cg.Channels.Select(c => c.Name))}");
}
```

### Log CAN / CAN FD frames

```csharp
using Mdf4Net.BusLogging;

CanDataFrameWriter can = writer.AddCanDataFrameGroup(new CanBusLoggingOptions { NetworkName = "CAN1" });
can.Write(0.0012, 0x181, pdoBytes);                                   // classic CAN
can.Write(0.0015, 0x18FF0102, fdBytes, CanFrameFlags.Extended | CanFrameFlags.FlexibleDataRate);

IReadOnlyList<CanDataFrame> frames = Mdf4File.Open("can.mf4").ReadCanDataFrames();
```

The group follows the ASAM bus logging layout (`CAN_DataFrame` with `ID`, `IDE`, `DLC`,
`DataLength`, `DataBytes`, `BusChannel`, `Dir`, `EDL`, `BRS`, `ESI`). The interop job checks that
asammdf's `extract_bus_logging` decodes such a file against a DBC; tools that follow the same ASAM
convention read it the same way.

## Format coverage

| Area | Read | Write |
| --- | :---: | :---: |
| MDF 4.00 / 4.10 / 4.20 identification, unfinalized files (`UnFinMF`) | ✅ | 4.10, 4.20 |
| Sorted data groups | ✅ | ✅ (one group per data group) |
| Unsorted data groups (record ids of 1/2/4/8 bytes) | ✅ | — |
| DT, DL, DZ (deflate, transposed deflate), HL | ✅ | ✅ |
| Integers 1–64 bit at any bit offset, little and big endian | ✅ | ✅ |
| Floats 16/32/64 bit, both byte orders | ✅ | ✅ |
| Strings (Latin-1, UTF-8, UTF-16 LE/BE), byte arrays, MIME | ✅ | ✅ |
| CANopen DATE / TIME | ✅ | — |
| Variable-length data: SD blocks | ✅ | ✅ |
| Variable-length data: VLSD channel groups | ✅ | — |
| Maximum-length channels, virtual master/data channels | ✅ | — |
| Invalidation bits | ✅ | — |
| Structures (CN compositions) | ✅ | ✅ |
| Arrays (CA blocks) | flagged, not expanded | — |
| Conversions: identity, linear, rational, value↔value tables, ranges, value/range→text, text→value, text→text, nested | ✅ | linear, rational, tables, ranges, value/range→text |
| Conversions: algebraic, bitfield text | `NotSupportedException` | — |
| Source information, file history, comments (TX and MD/XML) | ✅ | ✅ |
| Bus logging: CAN / CAN FD data frames | ✅ | ✅ |
| Column-oriented storage (DV/DI, MDF 4.20), attachments, events, sample reduction | — | — |
| MDF 3.x | — | — |

## Validation

The ASAM MDF specification is not freely available, so conformance is established against an
independent implementation, in both directions:

- **Interop job (CI):** `eng/interop/check_asammdf.py` has Mdf4Net write a set of scenario files
  (every data type and bit layout, the written conversions, block chaining, both compression
  modes, variable-length data, several groups, MDF 4.20, CAN and CAN FD including DBC decoding)
  and checks every value with [asammdf](https://github.com/danielhrisca/asammdf); then asammdf
  writes its own files and Mdf4Net reads them. The asammdf version is pinned in `eng/interop/requirements.txt`.
- **Fixtures:** files written by asammdf are committed under `tests/Mdf4Net.Tests/Fixtures`, so
  the reader is checked against them on every test run, on every platform.
- **Robustness:** the reader is driven through truncations of a sample file at every third byte,
  thousands of random corruptions and rewired block links; the only acceptable outcome is success or
  `Mdf4FormatException` (see [SECURITY.md](SECURITY.md)).

For an additional check on Windows, the free *Vector MDF Validator* can be run over files the
interop probe writes (`dotnet run --project tools/Mdf4Net.InteropProbe -- write <dir>`).

Mdf4Net was implemented from the publicly documented parts of the format and validated against the
behaviour of open implementations; it contains no code from them.

## Reading untrusted files

Every length and count in an MDF file is attacker-controlled when the file comes from elsewhere.
`Mdf4ReaderOptions` bounds what a reader allocates (metadata block size, decompressed block size,
record size, signal data per file, records of groups without stored data, nesting depth); block chains are checked for cycles; MD
blocks are parsed with DTD processing disabled. See [SECURITY.md](SECURITY.md) for the threat model
and how to report a problem.

## Supply chain

Each release ships a CycloneDX 1.6 SBOM enriched for BSI TR-03183-2, plus signed build-provenance
and SBOM attestations — see [docs/supply-chain.md](docs/supply-chain.md).

## Versioning and releases

[Semantic Versioning](https://semver.org/), computed from [Conventional Commits](https://www.conventionalcommits.org/)
by semantic-release — see [docs/release-process.md](docs/release-process.md) and the
[changelog](CHANGELOG.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Bug reports with a small `.mf4` file that shows the problem
are the most useful kind.

## License

[MIT](LICENSE). ASAM and MDF are names of ASAM e.V.; this project is not affiliated with or endorsed
by ASAM.
