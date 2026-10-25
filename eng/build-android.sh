#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -f "$project_root/.tools/env.sh" ]]; then source "$project_root/.tools/env.sh"; fi
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}${JAVA_HOME:+$JAVA_HOME/bin:}${ANDROID_HOME:+$ANDROID_HOME/platform-tools:}$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if command -v pwsh >/dev/null 2>&1; then
    powershell=pwsh
elif [[ -x "$project_root/.tools/powershell/pwsh" ]]; then
    powershell="$project_root/.tools/powershell/pwsh"
else
    printf 'PowerShell 7.4+ is required. Install it or run eng/setup-android.sh.\n' >&2
    exit 1
fi
exec "$powershell" -NoProfile -File "$project_root/build.ps1" "$@"
