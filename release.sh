#!/usr/bin/env bash
# Cuts releases by creating and pushing <platform>-v<version> tags at HEAD. Each pushed tag
# starts its own run of .github/workflows/release.yml, which builds that one target, publishes
# a GitHub release for the tag, and refreshes that platform's rolling "latest" release.
#
#   ./release.sh hk1221 0.3.3                  one platform
#   ./release.sh silksong=0.4.0 hk1578=0.3.4   several platforms, independent versions
#   ./release.sh --all 0.4.0                   every platform at one version
#
# --all also triggers the hk1578 modlinks version-bump PR, so reserve lockstep releases for
# changes that genuinely affect every platform; otherwise name the platforms you changed.
#
# Everything is validated before anything is created: an invalid platform, a malformed version,
# or an existing tag aborts the whole run, and the tags that do get created are pushed together.
set -euo pipefail

PLATFORMS="silksong hk1221 hk1432 hk1578"
REMOTE="${RELEASE_REMOTE:-origin}"

usage() {
  sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-1}"
}

die() { echo "release: $*" >&2; exit 1; }

valid_platform() {
  case " $PLATFORMS " in *" $1 "*) return 0 ;; *) return 1 ;; esac
}

valid_version() {
  # Semver: major.minor.patch, with optional pre-release and build metadata.
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?(\+[0-9A-Za-z.]+)?$ ]]
}

plain_version() {
  [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
}

[ $# -gt 0 ] || usage
case "${1:-}" in -h|--help) usage 0 ;; esac

pairs=()

if [ "$1" = "--all" ]; then
  [ $# -eq 2 ] || die "--all takes exactly one version."
  for platform in $PLATFORMS; do
    pairs+=("$platform=$2")
  done
elif [ $# -eq 2 ] && [[ "$1" != *=* ]]; then
  pairs+=("$1=$2")
else
  for arg in "$@"; do
    [[ "$arg" == *=* ]] || die "expected <platform>=<version>, got '$arg'."
    pairs+=("$arg")
  done
fi

# Validate every pair before creating a single tag, so a typo in the last argument cannot leave
# half a release pushed.
tags=()
for pair in "${pairs[@]}"; do
  platform="${pair%%=*}"
  version="${pair#*=}"
  valid_platform "$platform" || die "unknown platform '$platform' (expected one of: $PLATFORMS)."
  valid_version "$version" || die "'$version' is not a semver version (expected major.minor.patch)."
  # Thunderstore only accepts plain major.minor.patch, and rejects the package outright
  # otherwise - catch it here rather than half way through the release build.
  if [ "$platform" = "silksong" ] && ! plain_version "$version"; then
    die "silksong versions cannot have a pre-release or build suffix ('$version') - Thunderstore requires plain major.minor.patch."
  fi
  tag="$platform-v$version"
  case " ${tags[*]:-} " in *" $tag "*) die "'$tag' given twice." ;; esac
  if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    die "tag '$tag' already exists locally."
  fi
  if git ls-remote --exit-code --tags "$REMOTE" "$tag" >/dev/null 2>&1; then
    die "tag '$tag' already exists on $REMOTE."
  fi
  tags+=("$tag")
done

if [ -n "$(git status --porcelain)" ]; then
  die "working tree has uncommitted changes; commit or stash them first."
fi

commit="$(git rev-parse --short HEAD)"
echo "Tagging $commit:"
printf '  %s\n' "${tags[@]}"

for tag in "${tags[@]}"; do
  git tag -a "$tag" -m "${tag%%-v*} ${tag#*-v}"
done

git push "$REMOTE" "${tags[@]}"

echo "Pushed ${#tags[@]} tag(s) to $REMOTE; each one starts its own release build."
