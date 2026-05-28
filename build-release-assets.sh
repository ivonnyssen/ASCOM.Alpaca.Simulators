#!/usr/bin/env bash
#
# Build + package the OmniSim release assets consumed by rusty-photon's
# .github/actions/install-omnisim action (issue #326 patched fork).
#
# Produces, per platform, an archive named exactly like the upstream release
# assets, each containing a top-level `ascom.alpaca.simulators.<plat>/` directory
# with the self-contained binary inside — the layout install-omnisim extracts.
#
# Run from the FORK REPO ROOT, in an environment with internet access
# (api.nuget.org must be reachable to restore the per-RID runtime packs).
#
#   ./build-release-assets.sh                        # all 5 platforms
#   ./build-release-assets.sh linux-x64 osx-arm64    # a subset (by RID)
#
# Requires: .NET 8 SDK (`dotnet`); `xz` for .tar.xz targets; `zip` OR `python3`
# for .zip targets; `sha256sum`.
# Override the SDK with:  DOTNET=/path/to/dotnet ./build-release-assets.sh
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
PROJ="ASCOM.Alpaca.Simulators/ASCOM.Alpaca.Simulators.csproj"
OUT="bin/release-assets"        # final archives (under bin/ => gitignored)
STAGE="bin/release-staging"     # per-RID publish trees

# rid : asset-basename : archive-kind : install-omnisim label
MATRIX=(
  "linux-x64:linux-x64:tar.xz:Linux-X64"
  "linux-arm64:linux-aarch64:tar.xz:Linux-ARM64"
  "osx-arm64:macos-arm64:zip:macOS-ARM64"
  "osx-x64:macos-x64:zip:macOS-X64"
  "win-x64:windows-x64:zip:Windows-X64"
)

# Optional subset: keep only requested RIDs.
if [ "$#" -gt 0 ]; then
  want=" $* "
  filtered=()
  for e in "${MATRIX[@]}"; do
    rid="${e%%:*}"
    [[ "$want" == *" $rid "* ]] && filtered+=("$e")
  done
  MATRIX=("${filtered[@]}")
  [ "${#MATRIX[@]}" -gt 0 ] || { echo "ERROR: no matching RIDs in: $*"; exit 1; }
fi

# Tool checks, conditional on what the selected targets actually need.
need_xz=0; need_zip=0
for e in "${MATRIX[@]}"; do
  kind="$(echo "$e" | cut -d: -f3)"
  [ "$kind" = "tar.xz" ] && need_xz=1
  [ "$kind" = "zip" ] && need_zip=1
done
command -v "$DOTNET" >/dev/null || { echo "ERROR: dotnet not found (set DOTNET=...)"; exit 1; }
command -v sha256sum >/dev/null || { echo "ERROR: sha256sum not found"; exit 1; }
[ "$need_xz" = 0 ] || command -v xz >/dev/null || { echo "ERROR: xz not found (needed for .tar.xz)"; exit 1; }
if [ "$need_zip" = 1 ] && ! command -v zip >/dev/null && ! command -v python3 >/dev/null; then
  echo "ERROR: need 'zip' or 'python3' for .zip assets"; exit 1
fi

# Zip a staged subdir, preserving the Unix exec bit. Uses `zip` if present,
# else falls back to python3's zipfile. $1=stage dir, $2=subdir, $3=abs out path.
make_zip() {
  local stage="$1" subdir="$2" outzip="$3"
  if command -v zip >/dev/null; then
    ( cd "$stage" && zip -r -q "$outzip" "$subdir" )
  else
    ( cd "$stage" && python3 - "$outzip" "$subdir" <<'PY'
import os, sys, zipfile
out, top = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _dirs, files in os.walk(top):
        for name in files:
            p = os.path.join(root, name)
            zi = zipfile.ZipInfo.from_file(p, p)
            zi.external_attr = (os.stat(p).st_mode & 0xFFFF) << 16  # keep exec bit
            zi.compress_type = zipfile.ZIP_DEFLATED
            with open(p, "rb") as f:
                z.writestr(zi, f.read())
PY
    )
  fi
}

rm -rf "$OUT" "$STAGE"
mkdir -p "$OUT" "$STAGE"
root="$(pwd)"
declare -a SUMMARY

for entry in "${MATRIX[@]}"; do
  IFS=":" read -r RID BASE KIND LABEL <<<"$entry"
  subdir="ascom.alpaca.simulators.$BASE"
  asset="ascom.alpaca.simulators.$BASE.$KIND"
  echo ">>> [$LABEL] publish $RID -> $subdir"
  "$DOTNET" publish "$PROJ" -c Release -r "$RID" --self-contained true \
    -o "$STAGE/$subdir"

  echo ">>> [$LABEL] package $asset"
  if [ "$KIND" = "tar.xz" ]; then
    tar -cJf "$OUT/$asset" -C "$STAGE" "$subdir"
  else
    make_zip "$STAGE" "$subdir" "$root/$OUT/$asset"
  fi
  sha="$(sha256sum "$OUT/$asset" | awk '{print $1}')"
  SUMMARY+=("$LABEL|$asset|$sha")
done

echo
echo "=================================================================="
echo "Assets in $OUT/  (paste these into install-omnisim/action.yml):"
printf '%-13s %-44s %s\n' "LABEL" "ASSET" "SHA256"
for s in "${SUMMARY[@]}"; do
  IFS="|" read -r label asset sha <<<"$s"
  printf '%-13s %-44s %s\n' "$label" "$asset" "$sha"
done
echo "=================================================================="
echo "Next: gh release create <tag> $OUT/*  (on ivonnyssen/ASCOM.Alpaca.Simulators)"
