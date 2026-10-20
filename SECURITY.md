# Security policy

## Supported versions

Mdf4Net is released as a single NuGet package. Fixes go into the **latest** release; there are no
maintenance branches for older versions. Upgrading to the newest patch release is the supported way
to get a fix.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private reporting — *Security* → *Report a vulnerability* on
[this repository](https://github.com/dborgards/Mdf4Net/security/advisories/new). The report is
visible only to you and the maintainers until an advisory is published.

Helpful to include: the package version, what an attacker can achieve, and a reproduction. For a
reader problem the ideal reproduction is the smallest `.mf4` file that triggers it, attached to the
private report (not to a public issue).

You can expect an acknowledgement within a week. Fixes are released as soon as they are ready and
credited in the advisory unless you prefer otherwise.

## Scope

This repository covers the `Mdf4Net` package only. Problems in the .NET runtime, in
`System.IO.Compression` or in other dependencies belong to their maintainers; if you are unsure
which side a problem is on, report it here and it will be routed.

## Threat model

**MDF files are untrusted input.** Measurement files are exchanged between companies, downloaded
from loggers in the field and attached to tickets; a reader must assume any file was crafted.

The reader is expected to handle a malformed or hostile file by throwing `Mdf4FormatException`
(or `NotSupportedException` for a well-formed construct the library does not implement, such as an
algebraic conversion). Each of the following is treated as a **security issue**, not merely a bug:

- an exception of any other type escaping `Mdf4File.Open` or a read method
  (`IndexOutOfRangeException`, `OverflowException`, `NullReferenceException`, …);
- a hang or unbounded loop — for example on a cyclic block chain (`cn_cn_next`, `dl_dl_next`, …);
- unbounded memory allocation driven by a length or count field in the file — including a
  decompression bomb in a DZ block, an oversized block length, or an oversized VLSD length;
- stack exhaustion through deeply nested structures (compositions, nested conversions);
- any read outside the file, or any write to a location other than the stream given to the writer;
- XML processing of MD blocks that resolves external entities or DTDs.

The limits that bound allocation are configurable through `Mdf4ReaderOptions`; raising them is the
caller's decision, and a problem that only occurs after raising a limit far beyond its default is
still worth reporting.

Out of scope: the *content* of a valid file (wrong signal values, misleading names), and denial of
service by a file that is simply very large but well-formed.
