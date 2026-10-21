# Contributing to Mdf4Net

Thanks for helping. This page covers what is specific to this repository: how branches and
commits work (they decide releases), how to build and test, and how a change is checked against
another MDF implementation.

## Getting set up

```bash
git clone https://github.com/dborgards/Mdf4Net.git
cd Mdf4Net
dotnet build Mdf4Net.sln -c Release
dotnet test  Mdf4Net.sln -c Release
```

You need the **.NET 10 SDK or newer** (`global.json` pins `10.0.100` and rolls forward). The
interoperability check additionally needs Python 3.10+ and asammdf:

```bash
pip install -r eng/interop/requirements.txt
python3 eng/interop/check_asammdf.py
```

A local build produces version `0.0.0`, deliberately: the build never computes a version, it only
receives one from the pipeline or from semantic-release (see [docs/release-process.md](docs/release-process.md)).

## Branching

Trunk-based, one long-lived branch:

| Branch | Purpose |
| --- | --- |
| `main` | Always releasable. Releases are cut from it by hand (the *Release* workflow). |
| `feat/…`, `fix/…`, `docs/…` | Short-lived. Branch from `main`, open a pull request into `main`. |

## Commits decide the release

Pull requests are merged with a merge commit, and **every commit on the branch is analysed by
semantic-release** — not only the pull-request title. Write every commit as a
[Conventional Commit](https://www.conventionalcommits.org/), and keep the title one as well:

```
feat(reader): read DV/DI column-oriented data       -> minor release
fix(writer): align the last DZ block                -> patch release
feat(buslogging)!: rename CanDataFrameWriter.Write  -> major release
docs: explain the bus logging layout                -> no release
```

Scopes: `reader`, `writer`, `buslogging`, `blocks`, plus `docs`, `ci`, `build`, `deps`, `interop`.

A breaking change needs both the `!` and a footer saying what to do about it:

```
feat(writer)!: Mdf4Writer.Create takes the options first

BREAKING CHANGE: `Mdf4Writer.Create(path, options)` is now `Mdf4Writer.Create(options, path)`.
```

`docs`, `test`, `chore`, `ci`, `style` and `refactor` never release on their own. Use them
honestly: a behaviour change labelled `refactor` ships with no changelog entry and no version bump.

## Tests

Every behavioural change needs a test, and tests are deterministic (seeded randomness only).

- **Round trips** (`RoundTripTests`) write with the writer and read with the reader; they prove
  the two agree, not that either is right.
- **Independent evidence** comes from asammdf: the fixtures in `tests/Mdf4Net.Tests/Fixtures/asammdf`
  (regenerate with `python3 eng/interop/make_fixtures.py` when the asammdf pin moves) and the
  interop job. A change to how something is written or read should be visible there — add a
  scenario to `tools/Mdf4Net.InteropProbe/Scenarios.cs` or `eng/interop/check_asammdf.py`.
- **Layouts the writer does not produce** (unsorted groups, VLSD channel groups, invalidation bits)
  are built block by block with `RawMdf` in `tests/Mdf4Net.Tests/Infrastructure`.
- **Hostile input**: anything that parses a length, count or link belongs under the contract in
  `RobustnessTests` — success or `Mdf4FormatException`, nothing else.

### The `net48` leg

The library ships `netstandard2.0` as well as `net10.0`. On Windows the test project also targets
`net48`, which executes the `netstandard2.0` asset. To compile-check that leg elsewhere:

```bash
dotnet build tests/Mdf4Net.Tests -f net48 -p:Mdf4NetTestNetFrameworkLeg=true
```

## Public API

- XML documentation on every public type and member, in English; it is what IntelliSense shows.
- `PublicApiSurfaceTests` compares the public surface with
  `tests/Mdf4Net.Tests/ApiApprovals/Mdf4Net.approved.txt`. When you change the API on purpose,
  copy the generated `.received.txt` over the approved file in the same pull request — never edit
  the approved file by hand.
- Prefer adding an overload to changing a signature.

## Comments: the why, not the history

Say what the code does and why, in the present tense. Public XML documentation states the current
contract only. An inline comment may carry one issue or pull-request link as the pointer to its
history.

## License

Contributions are accepted under the [MIT License](LICENSE). There is no CLA — opening a pull
request licenses your contribution under the repository's license.
