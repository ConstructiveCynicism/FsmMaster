#!/usr/bin/env bash
# Cuts a release by creating and pushing a v<version> tag at HEAD, which starts
# .github/workflows/release.yml: it builds every platform the release covers, attaches one
# FsmMaster-<platform>.zip per platform to a single GitHub release, and publishes the platforms
# that have somewhere to publish to.
#
#   ./release.sh 0.3.5                      every platform
#   ./release.sh 0.3.5 hk1578 silksong      only these two
#
# Which platforms are in a release is also what gets published: include silksong and it goes to
# Thunderstore, include hk1578 and it opens a modlinks PR. Leave a platform out and it is neither
# built nor published - so name only what your change actually affects.
#
# Everything is validated before the tag is created: a bad platform name, a malformed version, an
# existing tag, or a dirty working tree aborts without creating anything.
set -euo pipefail

ALL_PLATFORMS="silksong hk1221 hk1315 hk1432 hk1578"
REMOTE="${RELEASE_REMOTE:-origin}"

usage() {
  sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-1}"
}

die() { echo "release: $*" >&2; exit 1; }

[ $# -gt 0 ] || usage
case "${1:-}" in -h|--help) usage 0 ;; esac

version="$1"
shift

# Semver: major.minor.patch, with optional pre-release and build metadata.
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?(\+[0-9A-Za-z.]+)?$ ]] \
  || die "'$version' is not a semver version (expected major.minor.patch)."

platforms=()
if [ $# -eq 0 ]; then
  for platform in $ALL_PLATFORMS; do platforms+=("$platform"); done
else
  for platform in "$@"; do
    case " $ALL_PLATFORMS " in
      *" $platform "*) ;;
      *) die "unknown platform '$platform' (expected one of: $ALL_PLATFORMS)." ;;
    esac
    case " ${platforms[*]:-} " in
      *" $platform "*) die "'$platform' given twice." ;;
    esac
    platforms+=("$platform")
  done
fi

# Thunderstore only accepts plain major.minor.patch and rejects the package outright otherwise -
# catch it here rather than half way through the release build.
case " ${platforms[*]} " in
  *" silksong "*)
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
      || die "a release including silksong cannot use a pre-release or build suffix ('$version') - Thunderstore requires plain major.minor.patch." ;;
esac

tag="v$version"
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  die "tag '$tag' already exists locally."
fi
if git ls-remote --exit-code --tags "$REMOTE" "$tag" >/dev/null 2>&1; then
  die "tag '$tag' already exists on $REMOTE."
fi

if [ -n "$(git status --porcelain)" ]; then
  die "working tree has uncommitted changes; commit or stash them first."
fi

echo "Tagging $(git rev-parse --short HEAD) as $tag, covering: ${platforms[*]}"

# The workflow reads the platform list back out of this message, so keep the "platforms:" line
# exactly as it is.
git tag -a "$tag" -F - <<EOF
Release $version

platforms: ${platforms[*]}
EOF

git push "$REMOTE" "$tag"

echo "Pushed $tag to $REMOTE; the release build covers: ${platforms[*]}"
