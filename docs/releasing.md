# Releasing BrainFudger

## Strategy

This repo now uses a lightweight semantic-versioning flow:

- `bugfix` work bumps `patch`
- `feature` work bumps `minor`
- `breaking` work bumps `major`
- prereleases use `-alpha.N`, `-beta.N`, or `-rc.N`
- final releases use plain `vX.Y.Z` tags

Examples:

- bugfix release: `v1.4.3`
- feature prerelease: `v1.5.0-alpha.1`
- release candidate: `v1.5.0-rc.2`
- final release after prereleases: `v1.5.0`

The GitHub release workflow only runs for tags matching `v*`.

## Build Version Metadata

When the release workflow builds a tagged release:

- package version comes from the tag without the leading `v`
- assembly version and file version use the numeric part only
- informational version includes the full tag plus commit metadata

Example for tag `v1.5.0-rc.2` on commit `abc1234` with commit count `87`:

- package version: `1.5.0-rc.2`
- assembly version: `1.5.0.0`
- file version: `1.5.0.0`
- informational version: `v1.5.0-rc.2+build.87.sha.abc1234`

## Tagging Workflow

Use the **Cut Release Tag** workflow in the GitHub Actions tab.

Inputs:

- `release_ref`: branch or commit to tag, usually `master`
- `change_type`: `bugfix`, `feature`, or `breaking`
- `release_type`: `final`, `alpha`, `beta`, or `rc`
- `base_version`: optional `X.Y.Z` to continue or promote a specific release line

How it behaves:

- if `base_version` is blank, the workflow looks at the latest stable `vX.Y.Z` tag and bumps:
  - `bugfix` => patch
  - `feature` => minor
  - `breaking` => major
- if `release_type` is `final`, it creates `vX.Y.Z`
- if `release_type` is `alpha`, `beta`, or `rc`, it creates `vX.Y.Z-alpha.N`, `vX.Y.Z-beta.N`, or `vX.Y.Z-rc.N`
- if `base_version` is supplied, the workflow uses that release line directly instead of bumping from the latest stable tag

The workflow pushes the tag for you. That tag push then triggers the normal release build workflow.

## Typical Flows

### Bugfix release

Run **Cut Release Tag** with:

- `release_ref`: `master`
- `change_type`: `bugfix`
- `release_type`: `final`
- `base_version`: blank

Example result: `v1.4.3`

### Feature preview

Run **Cut Release Tag** with:

- `release_ref`: `master`
- `change_type`: `feature`
- `release_type`: `alpha`
- `base_version`: blank

Example result: `v1.5.0-alpha.1`

### Continue a prerelease line

If the release line is `1.5.0`, run **Cut Release Tag** with:

- `release_ref`: `master`
- `change_type`: any value
- `release_type`: `beta`
- `base_version`: `1.5.0`

Example result: `v1.5.0-beta.1`, then `v1.5.0-beta.2`, and so on.

### Promote preview line to final

If the prerelease line was `1.5.0`, run **Cut Release Tag** with:

- `release_ref`: `master`
- `change_type`: any value
- `release_type`: `final`
- `base_version`: `1.5.0`

Example result: `v1.5.0`

## Notes

- prerelease tags become GitHub prereleases automatically
- final tags become normal GitHub releases automatically
- the release workflow builds all supported host artifacts and uploads them to the GitHub Release
