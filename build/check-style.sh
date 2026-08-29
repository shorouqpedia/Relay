#!/usr/bin/env bash
#
# The two rules ADR 0009 states that .editorconfig cannot express.
#
# Both are about shapes, not syntax, so no analyzer ships with them:
#
#   1. #region — a region labelled "validation" is a method waiting to be
#      extracted. Collapsing the symptom in an editor leaves the method long.
#
#   2. catch (Exception) with no filter — expected failures are Result values
#      (ADR 0005) and unexpected ones belong to the global handler, so a handler
#      that catches everything hides which of the two it is producing.
#
# The second rule has genuine exceptions, and they are opted into explicitly with
# a marker comment rather than by an allow-list in this file. A list here would
# drift from the code and would let a broad catch appear in a file that was
# already on it; the marker sits on the line it excuses and has to be written by
# whoever adds the catch.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="${1:-$root/src}"
failures=0

echo "Checking style rules under: $target"

# ── #region ──────────────────────────────────────────────────────────────────

region_hits="$(grep -rn --include='*.cs' -E '^\s*#(region|endregion)\b' "$target" || true)"

if [[ -n "$region_hits" ]]; then
    echo ""
    echo "FAIL: #region is not used in this codebase (ADR 0009)."
    echo "      A region around part of a method is a method waiting to be extracted."
    echo ""
    echo "$region_hits" | sed 's/^/      /'
    failures=$((failures + 1))
fi

# ── unfiltered catch of the base exception type ──────────────────────────────
#
# Matches `catch (Exception ...)` and bare `catch`, then drops the lines that
# carry a `when (` filter — a filtered catch is a specific decision, which is
# what the rule asks for.

broad_catches="$(grep -rn --include='*.cs' -E 'catch\s*(\(\s*Exception\b[^)]*\))?\s*($|\{)' "$target" \
    | grep -v 'when\s*(' || true)"

# Drop the ones that opted in on the same line or the line above.
unjustified=""

while IFS= read -r hit; do
    [[ -z "$hit" ]] && continue

    file="${hit%%:*}"
    rest="${hit#*:}"
    line="${rest%%:*}"

    # The marker may sit on the catch line itself or within the three lines above
    # it, which is where the explanation naturally goes.
    window_start=$(( line > 3 ? line - 3 : 1 ))
    window="$(sed -n "${window_start},${line}p" "$file")"

    if ! grep -q 'check-style: allow-broad-catch' <<<"$window"; then
        unjustified+="$hit"$'\n'
    fi
done <<<"$broad_catches"

if [[ -n "${unjustified// /}" ]]; then
    echo ""
    echo "FAIL: a catch of the base Exception type with no filter (ADR 0009)."
    echo "      Expected failures are Result values; unexpected ones belong to the"
    echo "      global handler. If this catch is genuinely justified, say so:"
    echo ""
    echo "          // check-style: allow-broad-catch — <why>"
    echo ""
    echo "$unjustified" | sed 's/^/      /'
    failures=$((failures + 1))
fi

# ── result ───────────────────────────────────────────────────────────────────

if (( failures > 0 )); then
    echo ""
    echo "$failures style rule(s) failed."
    exit 1
fi

echo "OK: no #region, no unjustified broad catches."
