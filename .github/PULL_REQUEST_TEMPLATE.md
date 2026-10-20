## What does this change?

<!-- One or two sentences. The "why" matters more than the "what" — the diff already shows the what. -->

## Type of change

<!--
Every commit on the branch must be a Conventional Commit, and so must the PR title.

Pull requests land as merge commits and every branch commit is retained, so semantic-release
analyses all of them -- a `docs:` title does not stop a `feat:` commit inside from publishing a
release. A breaking change needs its `BREAKING CHANGE:` footer in the *commit message*. Examples:

  feat(reader): read DV/DI column-oriented data
  fix(writer): align the last DZ block
  feat(buslogging)!: rename CanDataFrameWriter.Write      <- `!` = breaking, bumps the major
  docs: document the bus logging layout
-->

- [ ] `feat` — new behaviour (minor release)
- [ ] `fix` / `perf` — bug or performance fix (patch release)
- [ ] `docs` / `test` / `refactor` / `chore` / `ci` — no release
- [ ] Breaking change (`!` in the title, plus a `BREAKING CHANGE:` footer explaining the migration)

## Checklist

<!-- Tick a box only for something actually run. -->

- [ ] `dotnet build Mdf4Net.sln -c Release -p:CI=true` succeeds
- [ ] `dotnet test Mdf4Net.sln -c Release` passes
- [ ] `dotnet format Mdf4Net.sln --verify-no-changes` is clean
- [ ] `python3 eng/interop/check_asammdf.py` passes (for reader/writer changes)
- [ ] Public API changes are documented with XML comments and approved in `ApiApprovals/`
- [ ] New behaviour is covered by a test
