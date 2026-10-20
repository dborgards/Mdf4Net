// Fails the build if .releaserc.json references a plugin or changelog preset that the pinned
// tooling in package.json cannot actually load, or if the release rules publish something they
// should not.
//
// Deliberately not a full `semantic-release --dry-run`: that talks to the GitHub API and needs a
// token with push rights, which a pull-request build (especially from a fork) does not have. The
// release workflow runs the real dry run on `main`. What this catches is what a pull request *can*
// introduce: a typo'd plugin name, a plugin dropped from package.json, a preset/tooling mismatch,
// or a release rule that no longer tells test tooling apart from shipped dependencies.
//
// Optional argument: the version semantic-release is about to publish (wired up as the exec
// plugin's verifyReleaseCmd), checked to be plain SemVer.

import { readFileSync } from 'node:fs';

const config = JSON.parse(readFileSync(new URL('../.releaserc.json', import.meta.url), 'utf8'));

const problems = [];

// import.meta.resolve, not require.resolve: the plugins load presets via `import-from-esm`, and
// conventional-changelog-conventionalcommits is ESM-only from 10.x on.
const check = (what, id) => {
  try {
    import.meta.resolve(id);
    console.log(`ok       ${what} ${id}`);
  } catch {
    problems.push(`${what} ${id} cannot be resolved`);
    console.log(`MISSING  ${what} ${id}`);
  }
};

for (const entry of config.plugins ?? []) {
  const [name, options] = Array.isArray(entry) ? entry : [entry, {}];
  check('plugin', name);

  const preset = options?.preset;
  if (preset) check('preset', `conventional-changelog-${preset}`);
}

if (!Array.isArray(config.branches) || config.branches.length === 0) {
  problems.push('branches must list at least one release branch');
}

// What a Dependabot commit publishes. A shipped dependency moving is a patch; the `test-tooling`
// group (.github/dependabot.yml) changes nothing that ships, so it publishes nothing. Run through
// the analyser itself, with the configured rules, because the matching is not obvious: a later
// `release: false` rule beats an earlier match, and the glob negation lives in the subject.
const analyzerOptions = (config.plugins ?? [])
  .filter(Array.isArray)
  .find(([name]) => name === '@semantic-release/commit-analyzer')?.[1];

if (analyzerOptions) {
  const { analyzeCommits } = await import('@semantic-release/commit-analyzer');
  const expectRelease = async (subject, expected) => {
    const commit = { message: subject, subject, body: '', hash: '0' };
    const actual = await analyzeCommits(analyzerOptions, {
      commits: [commit],
      logger: { log() {} },
      cwd: process.cwd(),
    });
    if (actual !== expected) {
      problems.push(`"${subject}" would publish ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
    } else {
      console.log(`ok       "${subject}" -> ${JSON.stringify(expected)}`);
    }
  };
  await expectRelease('build(deps): Bump the test-tooling group with 1 update', null);
  await expectRelease('build(deps): Bump the test-tooling group across 1 directory with 2 updates', null);
  await expectRelease('build(deps): Bump the test-tooling-security group with 1 update', null);
  await expectRelease('build(deps): Bump System.Memory from 4.6.2 to 4.6.3', 'patch');
  await expectRelease('chore(deps): Bump the semantic-release group with 3 updates', null);
  await expectRelease('feat(reader): read unsorted data groups', 'minor');
  await expectRelease('fix(writer): align DZ blocks', 'patch');
  await expectRelease('feat(writer)!: rename Mdf4Writer.Create', 'major');
}

const requestedVersion = process.argv[2];
if (requestedVersion) {
  if (!/^\d+\.\d+\.\d+$/.test(requestedVersion)) {
    problems.push(`refusing to release "${requestedVersion}": not a plain X.Y.Z version`);
  } else {
    console.log(`ok       releasing ${requestedVersion}`);
  }
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s) in .releaserc.json:`);
  for (const p of problems) console.error(`  - ${p}`);
  process.exit(1);
}

console.log('\n.releaserc.json is loadable with the pinned tooling.');
