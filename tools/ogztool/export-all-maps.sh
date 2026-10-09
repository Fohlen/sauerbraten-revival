#!/usr/bin/env bash
# Export OGZ metadata for EVERY map in the reference dump.
#
# For each packages/<pkg>/<map>.ogz this runs the ogz tooling client once
# (SAUER_METADATA + SAUER_METADATA_EXIT) and writes
# metadata-catalogue/<map>.json, giving the catalogue one entry per map.
#
# Converted maps win: if assets/packages/<map>.ogz exists (see
# convert-all-maps.sh), it is staged into the client's home and exported instead
# of the dump copy, so the JSON describes the exact map the importer later reads.
#
# The client is built by tools/ogztool/build.sh; this script only orchestrates the
# runs. It needs a display + OpenGL context (the reference client is not headless),
# ~5 s per map.
#
# Usage:
#   tools/ogztool/export-all-maps.sh [options] [map ...]
#
# Options:
#   -n, --dry-run     list the maps that would be exported, then exit
#   -f, --force       re-export maps whose JSON already exists
#   -l, --limit N     export at most N maps this run (batch size; 0 = all)
#   -t SECONDS        per-map timeout (default 120)
#   -o DIR            output directory (default <repo>/metadata-catalogue)
#   -h, --help        show this help
#
# Runs are resumable: every map whose JSON already exists is skipped, so run
# the script repeatedly with -l to export in batches without blocking the
# machine for the full ~30 min sweep.
#
# Environment:
#   SAUER_REF   reference dump root (default: ~/Downloads/sauerbraten-code-r6889)
#   SAUER_HOME  throwaway client home (default: /tmp/sauer-meta-home)
#   CLIENT      ogz tooling client (default: tools/ogztool/out/sauer_client)
#
# With no map arguments every *.ogz under $SAUER_REF/packages is exported.
# Map names may be given bare (`church51`) or package-qualified (`base/church51`).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
REF="${SAUER_REF:-$HOME/Downloads/sauerbraten-code-r6889}"
HOME_DIR="${SAUER_HOME:-/tmp/sauer-meta-home}"
CLIENT="${CLIENT:-$HERE/out/sauer_client}"
OUT="$REPO/metadata-catalogue"
CONVERTED="$REPO/assets/packages"

DRY_RUN=0
FORCE=0
TIMEOUT=120
LIMIT=0

usage() { sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

MAPS=()
while [ $# -gt 0 ]; do
    case "$1" in
        -n|--dry-run) DRY_RUN=1 ;;
        -f|--force)   FORCE=1 ;;
        -l|--limit)   LIMIT="$2"; shift ;;
        -t)           TIMEOUT="$2"; shift ;;
        -o)           OUT="$2"; shift ;;
        -h|--help)    usage 0 ;;
        -*)           echo "unknown option: $1" >&2; usage 1 ;;
        *)            MAPS+=("$1") ;;
    esac
    shift
done

case "$LIMIT" in ''|*[!0-9]*) echo "ERROR: --limit must be a non-negative integer" >&2; exit 1;; esac

[ -d "$REF/packages" ] || { echo "ERROR: reference dump not found at '$REF' (set SAUER_REF)" >&2; exit 1; }
[ -x "$CLIENT" ]       || { echo "ERROR: ogz tooling client missing at '$CLIENT'; run tools/ogztool/build.sh" >&2; exit 1; }

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
ALL=()
while IFS= read -r line; do ALL+=("$line"); done < <(discover)

# Filter maps whose JSON already exists unless --force.
TODO=()
SKIPPED=0
for m in "${ALL[@]}"; do
    if [ "$FORCE" -eq 0 ] && [ -s "$OUT/$(basename "$m").json" ]; then
        SKIPPED=$((SKIPPED + 1))
        continue
    fi
    TODO+=("$m")
done

echo "reference: $REF"
echo "client:    $CLIENT"
echo "output:    $OUT"

# Trim to the batch size (after skipping up-to-date maps, so -l counts real work).
REMAINING=${#TODO[@]}
if [ "$LIMIT" -gt 0 ] && [ "${#TODO[@]}" -gt "$LIMIT" ]; then
    TODO=("${TODO[@]:0:$LIMIT}")
fi

echo "maps:      ${#ALL[@]} total, ${#TODO[@]} to export this run, $SKIPPED up-to-date, $((REMAINING - ${#TODO[@]})) left for later"

if [ "$DRY_RUN" -eq 1 ]; then
    [ "${#TODO[@]}" -gt 0 ] && printf '%s\n' "${TODO[@]}"
    exit 0
fi
[ "${#TODO[@]}" -gt 0 ] || { echo "nothing to do"; exit 0; }

# Export one map to <OUT>/<basename>.json. Returns non-zero on failure.
#
# IMPORTANT: the map may have a converted copy under assets/packages (produced by
# convert-all-maps.sh). The client's findfile() checks its home dir before the
# reference dump, so we stage the converted copy into the throwaway home — the
# metadata MUST be exported from the SAME map the reader later loads. calclight
# remips and regenerates surfaces, so a converted map's geometry differs from the
# dump's; exporting from the dump would make the catalogue's geometry block
# disagree with the map the importer reads.
export_one() {
    local m="$1"
    local dst="$OUT/$(basename "$m").json"
    local tmp="$dst.tmp.$$"
    local log="${TMPDIR:-/tmp}/sauer-meta-$(echo "$m" | tr '/' '_').log"

    rm -f "$tmp"
    rm -rf "$HOME_DIR/packages"
    if [ -f "$CONVERTED/$m.ogz" ]; then
        mkdir -p "$HOME_DIR/packages/$(dirname "$m")"
        cp "$CONVERTED/$m.ogz" "$HOME_DIR/packages/$m.ogz"
    fi

    if ( cd "$REF" && SAUER_METADATA="$tmp" SAUER_METADATA_EXIT=1 \
            timeout "$TIMEOUT" "$CLIENT" -q"$HOME_DIR" -l"$m" >"$log" 2>&1 ); then
        if [ -s "$tmp" ]; then
            mv "$tmp" "$dst"
            return 0
        fi
        echo "ERROR: $m produced no metadata (see $log)" >&2
    else
        echo "ERROR: $m failed or timed out (see $log)" >&2
    fi
    rm -f "$tmp"
    return 1
}

OK=0
FAIL=0
FAILED=()
for m in "${TODO[@]}"; do
    if export_one "$m"; then
        OK=$((OK + 1))
        printf '  ok   %s\n' "$m"
    else
        FAIL=$((FAIL + 1))
        FAILED+=("$m")
        printf '  FAIL %s\n' "$m"
    fi
done

echo
echo "done: $OK exported, $FAIL failed, $SKIPPED skipped (already up-to-date)"
if [ "$REMAINING" -gt "$OK" ] 2>/dev/null; then
    LEFT=$((REMAINING - OK))
    [ "$LEFT" -gt 0 ] && echo "$LEFT maps remaining — re-run to continue"
fi
if [ "$FAIL" -gt 0 ]; then
    echo "failed maps:"
    printf '  %s\n' "${FAILED[@]}"
    exit 1
fi
