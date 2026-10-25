#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tools_dir="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/develivestreamer/toolchain}"
mkdir -p "$tools_dir/downloads" "$project_root/.tools"
tools_dir="$(cd "$tools_dir" && pwd)"
if ! command -v pwsh >/dev/null 2>&1 && [[ ! -x "$project_root/.tools/powershell/pwsh" ]]; then
    powershell_version=7.6.9
    powershell_archive="powershell-$powershell_version-linux-x64.tar.gz"
    powershell_url="https://github.com/PowerShell/PowerShell/releases/download/v$powershell_version"
    curl -fsSL "$powershell_url/$powershell_archive" -o "$tools_dir/downloads/$powershell_archive"
    curl -fsSL "$powershell_url/hashes.sha256" -o "$tools_dir/downloads/powershell-hashes.sha256"
    python3 - "$tools_dir/downloads/$powershell_archive" "$tools_dir/downloads/powershell-hashes.sha256" <<'PY'
import hashlib, pathlib, sys
archive, manifest = map(pathlib.Path, sys.argv[1:])
raw = manifest.read_bytes()
text = raw.decode('utf-16' if raw.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig')
expected = next(line.split()[0] for line in text.splitlines() if line.rstrip().endswith(archive.name))
assert hashlib.sha256(archive.read_bytes()).hexdigest().lower() == expected.lower(), 'PowerShell archive verification failed'
PY
    mkdir -p "$project_root/.tools/powershell"
    tar -xzf "$tools_dir/downloads/$powershell_archive" -C "$project_root/.tools/powershell"
    chmod +x "$project_root/.tools/powershell/pwsh"
fi
if [[ ! -x "$tools_dir/dotnet/dotnet" ]]; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tools_dir/downloads/dotnet-install.sh"
    bash "$tools_dir/downloads/dotnet-install.sh" --version 10.0.402 --install-dir "$tools_dir/dotnet" --no-path
fi
if [[ ! -x "$tools_dir/java/bin/java" ]]; then
    curl -fsSL https://aka.ms/download-jdk/microsoft-jdk-21.0.8-linux-x64.tar.gz -o "$tools_dir/downloads/java.tar.gz"
    mkdir -p "$tools_dir/java"
    tar -xzf "$tools_dir/downloads/java.tar.gz" --strip-components=1 -C "$tools_dir/java"
fi
if [[ ! -x "$tools_dir/android-sdk/cmdline-tools/latest/bin/sdkmanager" ]]; then
    curl -fsSL https://dl.google.com/android/repository/commandlinetools-linux-16111833_latest.zip -o "$tools_dir/downloads/android-tools.zip"
    mkdir -p "$tools_dir/android-sdk/cmdline-tools"
    unzip -q "$tools_dir/downloads/android-tools.zip" -d "$tools_dir/android-sdk/cmdline-tools"
    mv "$tools_dir/android-sdk/cmdline-tools/cmdline-tools" "$tools_dir/android-sdk/cmdline-tools/latest"
fi
export DEVE_TOOLCHAIN_ROOT="$tools_dir"
python3 - "$project_root/.tools/env.sh" <<'PY'
import os, pathlib, shlex, sys
root=pathlib.Path(os.environ['DEVE_TOOLCHAIN_ROOT'])
values={'DOTNET_ROOT':root/'dotnet','JAVA_HOME':root/'java','ANDROID_HOME':root/'android-sdk','NUGET_PACKAGES':root/'nuget'}
text=''.join(f'export {name}={shlex.quote(str(value))}\n' for name,value in values.items())
text+='export PATH="$DOTNET_ROOT:$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$PATH"\nexport DOTNET_CLI_TELEMETRY_OPTOUT=1\n'
pathlib.Path(sys.argv[1]).write_text(text)
PY
source "$project_root/.tools/env.sh"
dotnet workload install maui-android --skip-manifest-update
sdkmanager="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
# This command explicitly accepts the SDK licenses for the environment being installed.
set +o pipefail
yes | "$sdkmanager" --sdk_root="$ANDROID_HOME" --licenses
set -o pipefail
"$sdkmanager" --sdk_root="$ANDROID_HOME" 'platforms;android-36' 'build-tools;36.0.0' 'platform-tools'
printf 'Ready. Run: source .tools/env.sh && ./eng/build-android.sh\n'
