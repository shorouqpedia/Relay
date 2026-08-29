#!/usr/bin/env bash
#
# Asserts that no checked-in configuration file carries a secret value.
#
# The convention this enforces: appsettings.json declares every secret-bearing
# key with an empty value, so the shape of the configuration is discoverable
# without a wiki, and the value comes from the environment. That convention is
# only worth anything if something checks it — the failure mode is one hurried
# commit, and a secret in git history outlives the commit that removed it.
#
# This is narrower than a general secret scanner and complements it. A scanner
# looks for things that resemble credentials anywhere; this looks at the exact
# keys this project knows are secrets and asserts they are empty. It catches the
# case a scanner misses: a short, unremarkable-looking value that happens to be a
# real password.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
failures=0

# Key names whose value must be empty in any committed settings file. Matched on
# the JSON key, so nesting does not matter.
secret_keys=(
    "ApiKey"
    "CallbackSecret"
    "SigningSecret"
    "Password"
    "ClientSecret"
    "ConnectionString"
)

echo "Checking committed configuration for secret values."

# appsettings.*.Local.json is git-ignored and is where a developer puts real
# values, so it is deliberately not searched — if one exists it should not be in
# the repository at all, which the next check covers.
#
# Collected null-separated rather than as a word-split string: this repository's
# own path contains a space, and an unquoted `for file in $(find ...)` splits it
# into two nonexistent paths. Every grep then "passes" because it found nothing
# in a file that does not exist — a check that silently stops checking.
config_files=()

while IFS= read -r -d '' file; do
    config_files+=("$file")
done < <(find "$root/src" "$root/tools" -name 'appsettings*.json' -not -name '*.Local.json' -print0 2>/dev/null)

for file in "${config_files[@]}"; do
    for key in "${secret_keys[@]}"; do
        # A non-empty string value for a secret-bearing key.
        hits="$(grep -nE "\"${key}\"[[:space:]]*:[[:space:]]*\"[^\"]+\"" "$file" || true)"

        if [[ -n "$hits" ]]; then
            echo ""
            echo "FAIL: ${file#"$root/"} sets '$key' to a value."
            echo "      Secret-bearing keys ship empty and are supplied by the environment."
            echo "$hits" | sed 's/^/      /'
            failures=$((failures + 1))
        fi
    done
done

# Connection strings are the common near-miss: the key is "Relay" rather than
# anything that looks like a secret, and the value carries a password.
for file in "${config_files[@]}"; do
    hits="$(grep -niE '"[^"]*"[[:space:]]*:[[:space:]]*"[^"]*(password|pwd)=' "$file" || true)"

    if [[ -n "$hits" ]]; then
        echo ""
        echo "FAIL: ${file#"$root/"} contains a connection string with a password."
        echo "$hits" | sed 's/^/      /'
        failures=$((failures + 1))
    fi
done

# A local settings file that reached the repository. It is git-ignored, so its
# presence here means someone forced it in.
tracked_local="$(git -C "$root" ls-files '*appsettings*.Local.json' 2>/dev/null || true)"

if [[ -n "$tracked_local" ]]; then
    echo ""
    echo "FAIL: a local settings file is tracked by git."
    echo "$tracked_local" | sed 's/^/      /'
    failures=$((failures + 1))
fi

if (( failures > 0 )); then
    echo ""
    echo "$failures configuration check(s) failed."
    exit 1
fi

echo "OK: no secret values in committed configuration."
