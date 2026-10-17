#!/usr/bin/env bash
# Convert pre-v32 OGZ maps to the current MAPVERSION (v33), so the offline
# importer only ever reads v32+ (see docs/adr/0006-ogz-import.md).
#
# For each packages/<pkg>/<map>.ogz with version < 32, this runs the ogz tooling
# client once (SAUER_CONVERT_SAVE + SAUER_METADATA_EXIT). The client loads the
# old map (the engine owns the whole legacy decode ladder), RECALCULATES its
# lighting — regenerating the modern surfaceinfo/vertinfo link and a fresh atlas
# set — and re-saves it at v33. `calclight` is mandatory: it is what produces a
# self-consistent surface<->atlas pairing; a save without it would carry stale
# lightmap links.
#
# Writes go to a scratch home (findfile() prefers $homedir), so the read-only
# reference dump is NEVER modified. Output mirrors the source package layout
# under assets/ (which carries a .gdignore so Godot never scans the raw OGZ):
#   assets/packages/<pkg>/<map>.ogz
#
# The client is built by tools/ogztool/build.sh; this script only orchestrates
# the runs. It needs a display + OpenGL context (the reference client is not
# headless). Recalc makes each run slower than a plain metadata export.
#
# Usage:
#   tools/ogztool/convert-all-maps.sh [options] [map ...]
#
# Options:
#   -n, --dry-run     list the maps that would be converted, then exit
#   -f, --force       re-convert maps whose output already exists
#   -a, --all         convert every map regardless of version (v33 included)
#   -t SECONDS        per-map timeout (default 600; recalc is the slow part)
#   -o DIR            output root (default <repo>/assets/packages)
#   -h, --help        show this help
#
# Environment:
#   SAUER_REF   reference dump root (default: ~/Downloads/sauerbraten-code-r6889)
#   SAUER_HOME  throwaway client home (default: /tmp/sauer-conv-home)
#   CLIENT      ogz tooling client (default: tools/ogztool/out/sauer_client)
#
# With no map arguments every *.ogz under $SAUER_REF/packages is considered.
# Map names may be given bare (`campo`) or package-qualified (`base/campo`).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
REF="${SAUER_REF:-$HOME/Downloads/sauerbraten-code-r6889}"
HOME_DIR="${SAUER_HOME:-/tmp/sauer-conv-home}"
CLIENT="${CLIENT:-$HERE/out/sauer_client}"
OUT="$REPO/assets/packages"

DRY_RUN=0
FORCE=0
ALL=0
TIMEOUT=600

usage() { sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

MAPS=()
while [ $# -gt 0 ]; do
    case "$1" in
        -n|--dry-run) DRY_RUN=1 ;;
        -f|--force)   FORCE=1 ;;
        -a|--all)     ALL=1 ;;
        -t)           TIMEOUT="$2"; shift ;;
        -o)           OUT="$2"; shift ;;
        -h|--help)    usage 0 ;;
        -*)           echo "unknown option: $1" >&2; usage 1 ;;
        *)            MAPS+=("$1") ;;
    esac
    shift
done

[ -d "$REF/packages" ] || { echo "ERROR: reference dump not found at '$REF' (set SAUER_REF)" >&2; exit 1; }
[ -x "$CLIENT" ]       || { echo "ERROR: ogz tooling client missing at '$CLIENT'; run tools/ogztool/build.sh" >&2; exit 1; }

# Read the octaheader version of an OGZ (7th int, little-endian, after gunzip).
ogz_version() {
    python3 - "$1" <<'PY'
import gzip, struct, sys
with gzip.open(sys.argv[1], "rb") as f:
    b = f.read(28)
if b[:4] != b"OCTA":
    print("bad"); raise SystemExit
print(struct.unpack_from("<i", b, 4)[0])
PY
}

# Resolve the map list to package-qualified names (relative to packages/, no .ogz).
discover() {
    if [ "${#MAPS[@]}" -gt 0 ]; then
        local m
        for m in "${MAPS[@]}"; do
            m="${m%.ogz}"
            if [ -f "$REF/packages/$m.ogz" ]; then
                printf '%s\n' "$m"
            elif [ -f "$REF/packages/base/$m.ogz" ]; then
                printf 'base/%s\n' "$m"
            else
                echo "WARNING: no such map: $m" >&2
            fi
        done
    else
        (cd "$REF/packages" && find . -name '*.ogz' -print | sed 's|^\./||; s|\.ogz$||' | sort)
    fi
}

mkdir -p "$OUT"

# Godot must not import the raw .ogz files as resources; .gdignore makes the
# asset importer skip this whole subtree. Written once, here, so the tree is
# self-describing even on a fresh checkout.
if [ ! -f "$REPO/assets/.gdignore" ]; then printf '' > "$REPO/assets/.gdignore"; fi

TODO=()
SKIPPED_STALE=0
SKIPPED_DONE=0
for m in $(discover); do
    src="$REF/packages/$m.ogz"
    [ -f "$src" ] || continue

    ver="$(ogz_version "$src")"
    if [ "$ALL" -eq 0 ] && [ "$ver" != "bad" ] && [ "$ver" -ge 32 ]; then
        SKIPPED_STALE=$((SKIPPED_STALE + 1))
        continue
    fi

    dst="$OUT/$m.ogz"
    if [ "$FORCE" -eq 0 ] && [ -s "$dst" ]; then
        SKIPPED_DONE=$((SKIPPED_DONE + 1))
        continue
    fi
    TODO+=("$m")
done

echo "reference: $REF"
echo "client:    $CLIENT"
echo "output:    $OUT"
echo "maps:      ${#TODO[@]} to convert, $SKIPPED_STALE already v32+ (skipped), $SKIPPED_DONE already converted"

if [ "$DRY_RUN" -eq 1 ]; then
    [ "${#TODO[@]}" -gt 0 ] && printf '%s\n' "${TODO[@]}"
    exit 0
fi
[ "${#TODO[@]}" -gt 0 ] || { echo "nothing to do"; exit 0; }

# Convert one map into <OUT>/<pkg>/<map>.ogz (the path already carries the
# package because $m is package-qualified). Returns non-zero on failure.
convert_one() {
    local m="$1"
    local dst="$OUT/$m.ogz"
    local log="${TMPDIR:-/tmp}/sauer-conv-$(echo "$m" | tr '/' '_').log"

    rm -rf "$HOME_DIR"
    mkdir -p "$HOME_DIR"

    if ( cd "$REF" && SAUER_CONVERT_SAVE=1 SAUER_METADATA_EXIT=1 \
            timeout "$TIMEOUT" "$CLIENT" -q"$HOME_DIR" -l"$m" >"$log" 2>&1 ); then
        if [ -s "$HOME_DIR/packages/$m.ogz" ]; then
            mkdir -p "$(dirname "$dst")"
            mv "$HOME_DIR/packages/$m.ogz" "$dst"
            # Verify the output really is the current format.
            local outver
            outver="$(ogz_version "$dst")"
            if [ "$outver" != "33" ]; then
                echo "ERROR: $m converted to unexpected version $outver (see $log)" >&2
                rm -f "$dst"
                return 1
            fi
            return 0
        fi
        echo "ERROR: $m produced no output map (see $log)" >&2
    else
        echo "ERROR: $m failed or timed out (see $log)" >&2
    fi
    return 1
}

OK=0
FAIL=0
FAILED=()
for m in "${TODO[@]}"; do
    if convert_one "$m"; then
        OK=$((OK + 1))
        printf '  ok   %s\n' "$m"
    else
        FAIL=$((FAIL + 1))
        FAILED+=("$m")
        printf '  FAIL %s\n' "$m"
    fi
done

echo
echo "done: $OK converted, $FAIL failed, $SKIPPED_STALE skipped (already v32+), $SKIPPED_DONE already converted"
if [ "$FAIL" -gt 0 ]; then
    echo "failed maps:"
    printf '  %s\n' "${FAILED[@]}"
    exit 1
fi
