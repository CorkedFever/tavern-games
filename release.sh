#!/usr/bin/env bash
#
# Cut a release: bump, build, package, verify, tag, publish, and update the manifest mirrors.
#
#   ./release.sh 0.1.0 --notes notes.md            # the real thing
#   ./release.sh 0.1.0 --notes notes.md --dry-run  # everything up to the point of no return
#
# The same shape as Aetherstream's release script, for the same reasons: the build's exit code
# is checked directly, the manifest inside the zip is asserted before upload, the zip never
# enters the working tree, and repo.json is only updated once the release actually exists.

set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-}"
NOTES=""
DRY_RUN=0
shift || true
while [ $# -gt 0 ]; do
    case "$1" in
        --notes)   NOTES="$2"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        *) echo "unknown argument: $1"; exit 2 ;;
    esac
done

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "usage: $0 <major.minor.patch> --notes <file> [--dry-run]"; exit 2; }
if [ "$DRY_RUN" = 0 ]; then
    [ -n "$NOTES" ] && [ -f "$NOTES" ] || { echo "release notes file required: --notes <file>"; exit 2; }
fi

REPO="CorkedFever/tavern-games"
PLUGIN="src/TavernGames.Plugin"
OUT_DIR="$PLUGIN/bin/Release/TavernGames"
TAG="v$VERSION"
ASSEMBLY="$VERSION.0"

say() { printf '\n== %s\n' "$*"; }
fail() { printf '\n!! %s\n' "$*" >&2; exit 1; }

say "preconditions"
[ "$(git rev-parse --abbrev-ref HEAD)" = "main" ] || fail "release from main, not $(git rev-parse --abbrev-ref HEAD)"
[ -z "$(git status --porcelain)" ] || fail "working tree is not clean; commit or stash first"
git fetch -q origin main
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || fail "main is not in sync with origin; push or pull first"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && fail "tag $TAG already exists"
command -v gh >/dev/null || fail "gh is required"
command -v dotnet >/dev/null || fail "dotnet is required"
command -v python >/dev/null || fail "python is required"
gh auth status >/dev/null 2>&1 || fail "gh is not signed in"

say "bumping to $ASSEMBLY"
python - "$VERSION" "$ASSEMBLY" "$TAG" <<'PY'
import io, json, re, sys, time
version, assembly, tag = sys.argv[1], sys.argv[2], sys.argv[3]

p = 'src/TavernGames.Plugin/TavernGames.Plugin.csproj'
s = io.open(p, encoding='utf-8').read()
s = re.sub(r'<Version>[^<]*</Version>', f'<Version>{version}</Version>', s)
s = re.sub(r'<AssemblyVersion>[^<]*</AssemblyVersion>', f'<AssemblyVersion>{assembly}</AssemblyVersion>', s)
s = re.sub(r'<FileVersion>[^<]*</FileVersion>', f'<FileVersion>{assembly}</FileVersion>', s)
io.open(p, 'w', encoding='utf-8', newline='\n').write(s)

for p in ('repo.json', 'docs/repo.json'):
    entries = json.load(io.open(p, encoding='utf-8'))
    e = entries[0]
    link = f'https://github.com/CorkedFever/tavern-games/releases/download/{tag}/TavernGames.zip'
    e['AssemblyVersion'] = assembly
    e['TestingAssemblyVersion'] = assembly
    e['DownloadLinkInstall'] = e['DownloadLinkUpdate'] = e['DownloadLinkTesting'] = link
    e['LastUpdate'] = int(time.time())
    io.open(p, 'w', encoding='utf-8', newline='\n').write(json.dumps(entries, indent=2) + '\n')
PY

say "building"
dotnet build TavernGames.slnx -c Release -nologo > /tmp/tavern-build.log 2>&1 || { tail -30 /tmp/tavern-build.log; fail "build failed"; }
grep -q "Build succeeded" /tmp/tavern-build.log || fail "build did not report success"

say "testing"
dotnet test TavernGames.slnx -c Release -nologo -v q > /tmp/tavern-test.log 2>&1 || { tail -30 /tmp/tavern-test.log; fail "tests failed"; }

say "verifying the package"
ZIP="$OUT_DIR/latest.zip"
[ -f "$ZIP" ] || fail "no package at $ZIP"
python - "$ZIP" "$ASSEMBLY" <<'PY'
import json, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
names = [n.replace('\\', '/') for n in z.namelist()]
# The icon is not in the zip on purpose: DalamudPackager keeps images/ beside it, and the installer takes IconUrl.
for required in ('TavernGames.dll', 'TavernGames.Core.dll', 'TavernGames.json', 'Fonts/VT323-Regular.ttf', 'Fonts/OFL.txt'):
    assert required in names, f'missing from zip: {required}'
manifest = json.loads(z.read('TavernGames.json'))
assert manifest['AssemblyVersion'] == sys.argv[2], f"zip manifest says {manifest['AssemblyVersion']}, expected {sys.argv[2]}"
assert manifest['Author'] == 'CorkedFever', manifest['Author']
print('   ok:', len(names), 'files, version', manifest['AssemblyVersion'])
PY

if [ "$DRY_RUN" = 1 ]; then
    say "dry run: stopping before commit, tag and release"
    git checkout -q -- "$PLUGIN/TavernGames.Plugin.csproj" repo.json docs/repo.json
    exit 0
fi

say "committing and tagging $TAG"
git add "$PLUGIN/TavernGames.Plugin.csproj" repo.json docs/repo.json
git commit -q -m "Release $VERSION"
git tag -a "$TAG" -m "Tavern Games $VERSION"

say "publishing"
# The tag goes up before the release is created: given a tag GitHub doesn't have yet,
# gh would mint its own on the default branch's head, one commit behind the bump.
git push -q origin main "$TAG"
STAGED="$(mktemp -d)"
cp "$ZIP" "$STAGED/TavernGames.zip"
gh release create "$TAG" "$STAGED/TavernGames.zip" --repo "$REPO" --title "Tavern Games $VERSION" --notes-file "$NOTES" --verify-tag
rm -rf "$STAGED"

say "asking GitHub Pages to rebuild"
gh api -X POST "repos/$REPO/pages/builds" >/dev/null || echo "   (Pages build request failed; it will pick the push up on its own)"

say "done: $TAG"
echo "   https://github.com/$REPO/releases/tag/$TAG"
