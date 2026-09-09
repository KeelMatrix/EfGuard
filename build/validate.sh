#!/usr/bin/env bash
set -euo pipefail

export KEELMATRIX_NO_TELEMETRY=1
script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec pwsh -NoLogo -NoProfile -File "$script_dir/validate.ps1" "$@"
