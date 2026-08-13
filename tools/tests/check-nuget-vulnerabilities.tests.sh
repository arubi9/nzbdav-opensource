#!/usr/bin/env bash
set -Eeuo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
PARSER="$ROOT/tools/check-nuget-vulnerabilities.py"

expect_exit() {
    local expected=$1
    shift
    local actual
    set +e
    "$@" >/dev/null 2>&1
    actual=$?
    set -e
    if [[ $actual -ne $expected ]]; then
        echo "expected exit $expected, got $actual: $*" >&2
        exit 1
    fi
}

expect_exit 0 python3 "$PARSER" "$ROOT/tools/tests/nuget-vulnerabilities-safe.json"
expect_exit 0 python3 "$PARSER" "$ROOT/tools/tests/nuget-path-only-clean.json"
expect_exit 0 python3 "$PARSER" "$ROOT/tools/tests/nuget-missing-package-arrays.json"
for fixture in \
    nuget-vulnerabilities-top-level.json \
    nuget-vulnerabilities-transitive.json \
    nuget-vulnerabilities-sdk-top-level-requested-version.json; do
    expect_exit 1 python3 "$PARSER" "$ROOT/tools/tests/$fixture"
done
for fixture in \
    nuget-empty-report.json \
    nuget-empty-projects.json \
    nuget-missing-path.json \
    nuget-malformed-framework.json \
    nuget-missing-top-level-array.json \
    nuget-missing-transitive-array.json \
    nuget-wrong-types.json \
    nuget-duplicate-keys.json \
    nuget-vulnerabilities-top-level-missing-id.json \
    nuget-vulnerabilities-top-level-empty-id.json \
    nuget-vulnerabilities-top-level-unknown-field.json \
    nuget-vulnerabilities-top-level-requested-version-empty.json \
    nuget-vulnerabilities-top-level-requested-version-nonstring.json \
    nuget-vulnerabilities-top-level-wrong-resolved-version.json \
    nuget-vulnerabilities-top-level-missing-metadata.json \
    nuget-vulnerabilities-top-level-empty-metadata.json \
    nuget-vulnerabilities-top-level-partial-metadata.json \
    nuget-vulnerabilities-top-level-unknown-metadata.json \
    nuget-vulnerabilities-transitive-missing-id.json \
    nuget-vulnerabilities-transitive-empty-id.json \
    nuget-vulnerabilities-transitive-unknown-field.json \
    nuget-vulnerabilities-transitive-requested-version.json \
    nuget-vulnerabilities-transitive-wrong-resolved-version.json \
    nuget-vulnerabilities-transitive-missing-metadata.json \
    nuget-vulnerabilities-transitive-empty-metadata.json \
    nuget-vulnerabilities-transitive-partial-metadata.json \
    nuget-vulnerabilities-transitive-unknown-metadata.json \
    nuget-vulnerabilities-top-level-unknown-severity.json \
    nuget-vulnerabilities-transitive-unknown-severity.json \
    nuget-vulnerabilities-top-level-relative-advisory-url.json \
    nuget-vulnerabilities-transitive-relative-advisory-url.json; do
    expect_exit 2 python3 "$PARSER" "$ROOT/tools/tests/$fixture"
done
oversized=$(mktemp)
multi_dir=$(mktemp -d)
trap 'rm -f "$oversized"; rm -rf "$multi_dir"' EXIT
python3 - "$oversized" <<'PY'
import sys
with open(sys.argv[1], "wb") as report:
    report.write(b'{"version":1,"projects":[{"path":"clean.csproj"}]}')
    report.write(b" " * (10 * 1024 * 1024))
PY
cp "$ROOT/tools/tests/nuget-vulnerabilities-safe.json" "$multi_dir/clean.json"
cp "$ROOT/tools/tests/nuget-duplicate-keys.json" "$multi_dir/malformed.json"
if python3 "$PARSER" "$multi_dir/clean.json" "$multi_dir/malformed.json" >/dev/null 2>&1; then
    echo 'multi-file NuGet fixture should fail on its malformed report' >&2
    exit 1
fi
if python3 "$PARSER" "$oversized" >/dev/null 2>&1; then
    echo 'oversized NuGet fixture should fail the parser' >&2
    exit 1
fi
echo 'NuGet vulnerability parser fixtures (clean/path-only/top-level/transitive/requestedVersion/schema/multi-file/size): PASS'
