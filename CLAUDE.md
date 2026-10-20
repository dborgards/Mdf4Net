# Working agreement

## Pull requests

- **Every change lands through a pull request.** Nothing goes to `main` directly.
- **One pull request open at a time** — of deliberately authored work (Dependabot does not count).
  Each open pull request carries its own review threads, bot findings and coverage report, and
  every one of them has to be driven to closed; two in flight double the supervision and speed up
  nothing.
- **A pull request is finished when every thread is closed, not when the code is right.** Bot
  findings and the coverage report count, and so does a thread whose finding was fixed but which
  still shows no answer. A base merge re-triggers reviewers: re-check afterwards.
- **"FERTIG — mergebar" is what says a branch is finished.** Say it literally, and only once every
  thread is closed and every check the branch is answerable for is green. Anything that falsifies
  that state — a new finding, a push — revokes it at that moment: say so before investigating.
  After the signal, a further push is the maintainer's decision (see *Decisions* below).
- Branch from `main` as `feat/…`, `fix/…`, `docs/…`.
- **Every commit on the branch decides the release**, not the pull-request title (merge commits,
  all branch commits retained). Write each commit as a Conventional Commit; a breaking change
  needs `!` and a `BREAKING CHANGE:` footer **in the commit message**.
- Fill in `.github/PULL_REQUEST_TEMPLATE.md`. Tick a box only for something actually run.

## Verify locally before pushing

A .NET 10 SDK is installable in the agent container (`dotnet-install.sh --channel 10.0`, or
`apt-get install dotnet-sdk-10.0`), so there is no reason to let CI be the compiler. The gate:

```bash
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build  Mdf4Net.sln -c Release -p:CI=true          # CI=true turns warnings into errors
dotnet test   Mdf4Net.sln -c Release --no-build --framework net10.0
dotnet format Mdf4Net.sln --verify-no-changes            # the single arbiter of code style
out=$(mktemp -d) && dotnet pack Mdf4Net.sln -c Release -p:Version=9.9.9 -o "$out" \
  && python3 eng/verify-packages.py "$out" 9.9.9
pip install -r eng/interop/requirements.txt && python3 eng/interop/check_asammdf.py
```

Do not report a build or test result that was not run. API approval baselines are taken from the
generated `*.received.txt`, never hand-written. After editing XML (`.props`, `.csproj`) or YAML,
check well-formedness: a `--` inside an XML comment makes every project fail to load.

## Correctness of the format

The ASAM specification is not in this repository and must not be quoted into it. What a file
must look like is established by the publicly documented format (ASAM MDF wiki, up to 4.1) and by
agreement with asammdf in both directions (`eng/interop/check_asammdf.py`). When a reader or
writer change is not covered by an interop scenario, add one — a round trip through our own
reader and writer proves only that the two agree with each other.

## Untrusted input

Every reader change is held to SECURITY.md: a malformed file yields `Mdf4FormatException` (or
`NotSupportedException` for an unimplemented construct) — never another exception type, a hang,
or an allocation sized by a field in the file. New parsing code gets a case in `RobustnessTests`.

## Stay inside the task

Scope is decided by causality, not by which files the diff touched. A failure that does not
reproduce on the base revision is this branch's and is fixed in this pull request — not filed, not
split off. Problems the tooling merely surfaces (a test failing on the base revision too, a finding
about neighbouring code) go into an issue, and the pull request in hand is finished first.

## Before claiming something is true

- **A claim about a test needs a mutation, not an argument.** Break the code the test is about and
  watch it fail before writing that the test catches it.
- **A figure belongs in one place**; everywhere else points at it.
- **Provenance is checkable — check it** in the history before citing it.

## Decisions that belong to the maintainer

When something genuinely needs a decision, ask it as a question with selectable options and stop
until it is answered. Do not bury a decision in a status message. Conversely, when the right answer
is clear, do it instead of offering to.
