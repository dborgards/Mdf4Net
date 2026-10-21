# Release process

The version is computed from the commit history. Nobody edits a version number, and nobody decides
by hand what the next one is.

```
 conventional commits on main
            │
            ▼
   the `verify` job ──── asks the commit analyser for X.Y.Z
   (no credentials)  └── builds, tests, packs and writes the SBOM at that version ──┐
            │                                                                       │
            ▼                                                             .nupkg + .cdx.json
   semantic-release ─── writes CHANGELOG.md, commits it to main                     │
   (.releaserc.json)  ── tags vX.Y.Z                                                │
   (the only job with ── pushes the package it was handed ◄─────────────────────────┘
    publishing rights)   and opens the GitHub Release ─► nuget.org
            │
            ▼
   the `attest` job ──── signs build provenance and the SBOM for the package

 every other build
            │
            ▼
      GitVersion ─── derives an ordered prerelease version from tags + branch
   (GitVersion.yml)   e.g. 0.2.0-pr.17.3, so CI artifacts are identifiable
```

## The two tools

**semantic-release** answers *"what should the next release be, and what changed?"* It reads the
Conventional Commits since the last tag: `fix`/`perf` bump the patch, `feat` the minor, a `!` or a
`BREAKING CHANGE:` footer the major. It writes the changelog, creates the tag, publishes the GitHub
Release and pushes the package. It only runs on `main`.

**GitVersion** answers *"what version is this commit?"* for builds that are not releases. It runs
once per pipeline (the `version` job in `ci.yml`), never as an MSBuild task, and that job may fail:
an unnameable build falls back to `0.0.0-unversioned.<run>` and is still compiled and tested.

They meet at the tag: semantic-release writes `vX.Y.Z`, GitVersion reads it back.

Which commit types release what is in `.releaserc.json`; `eng/verify-release-config.mjs` checks on
every pull request that the configuration loads and that Dependabot's test-tooling bumps publish
nothing while a shipped dependency bump publishes a patch.

## Cutting a release

Releases are cut by hand, not by merging: *Actions* → *Release* → *Run workflow* on `main`.

1. Run it with **dry_run** ticked (the default). The log shows the version and the release notes
   semantic-release would publish.
2. Run it again with **dry_run** unticked. `verify` builds, tests, packs and generates the SBOM;
   `release` checks the package and the SBOM once more (`prepareCmd`), pushes to nuget.org, commits
   the changelog, moves the package-validation baseline, tags and opens the GitHub Release; `attest`
   signs provenance and SBOM.

If `attest` fails, *Re-run failed jobs* repeats it alone against the retained artifacts.

## First release

semantic-release starts at 1.0.0 when no `vX.Y.Z` tag exists. This repository starts with a 0.x
line instead: `v0.0.0` is tagged on the initial commit, so the first release with a `feat` commit
publishes **0.1.0**. While the major version is 0, a breaking change still bumps the major (to
1.0.0) under the configured rules; mark 1.0.0 deliberately rather than by accident.

The package-validation baseline (`PackageValidationBaselineVersion` in `src/Directory.Build.props`)
is empty until the first release and is moved by the release itself afterwards.

## One-time repository setup

| Setting | Where | What |
| --- | --- | --- |
| `RELEASE_TOKEN` | Actions secret | Fine-grained PAT, this repository only, *Contents: read and write*; used by semantic-release to push the tag, the changelog commit and the GitHub Release |
| `NUGET_USER` | Actions secret | The nuget.org account name for Trusted Publishing |
| Trusted Publishing policy | nuget.org → *Trusted Publishing* | Repository `dborgards/Mdf4Net`, workflow `release.yml` |
| `CODECOV_TOKEN` | Actions secret | Optional; coverage upload is skipped quietly without it |
| Branch ruleset on `main` | Settings → Rules | Require pull requests; required checks: the three OS legs, `format`, `pack (dry run)`, `interop (asammdf)` |
