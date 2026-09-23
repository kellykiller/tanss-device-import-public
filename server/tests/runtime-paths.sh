#!/usr/bin/env bash

set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
source "$repository_root/server/bin/runtime-paths"

temporary_directory="$(mktemp -d)"
trap 'rm -rf -- "$temporary_directory"' EXIT

env_file="$temporary_directory/.env"
cat >"$env_file" <<'EOF'
SINGLE_QUOTED='/srv/tanss data'
DOUBLE_QUOTED="/srv/tanss-secrets"
UNQUOTED=/srv/tanss-data
EMPTY=''
EOF

assert_equal() {
    local expected="$1"
    local actual="$2"
    local label="$3"

    if [[ "$actual" != "$expected" ]]; then
        echo "Fehler bei $label: erwartet '$expected', erhalten '$actual'" >&2
        exit 1
    fi
}

assert_equal "/srv/tanss data" \
    "$(tdi_dotenv_value SINGLE_QUOTED "$env_file")" \
    "einfach quotierter Wert"

assert_equal "/srv/tanss-secrets" \
    "$(tdi_dotenv_value DOUBLE_QUOTED "$env_file")" \
    "doppelt quotierter Wert"

assert_equal "/srv/tanss-data" \
    "$(tdi_dotenv_value UNQUOTED "$env_file")" \
    "unquotierter Wert"

assert_equal "/fallback" \
    "$(tdi_resolve_setting EMPTY "$env_file" /fallback)" \
    "Fallback für leeren Wert"

UNQUOTED="/environment-override"
export UNQUOTED
assert_equal "/environment-override" \
    "$(tdi_resolve_setting UNQUOTED "$env_file" /fallback)" \
    "Umgebungsüberschreibung"

tdi_require_absolute_path "/srv/tanss" "Testpfad"

if tdi_require_absolute_path "relative/path" "Testpfad" 2>/dev/null; then
    echo "Fehler: Relativer Pfad wurde akzeptiert." >&2
    exit 1
fi

echo "RUNTIME_PATH_TESTS_OK"
