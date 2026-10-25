#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -f "$project_root/.tools/env.sh" ]]; then source "$project_root/.tools/env.sh"; fi
: "${ANDROID_HOME:?Set ANDROID_HOME or run eng/setup-android.sh}"
: "${JAVA_HOME:?Set JAVA_HOME or run eng/setup-android.sh}"
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$project_root"
build_version="${BUILD_VERSION:-0.2.1}"
build_number="${BUILD_NUMBER:-4}"
output_dir="$project_root/artifacts/android"
mkdir -p "$output_dir" "$project_root/.tools"
dotnet test tests/DeveLiveStreamer.Core.Tests -c Release -p:RestoreLockedMode=true --logger trx --results-directory artifacts/test-results
# Streaming tests exchange real SRT/RTMP streams with FFmpeg when it is installed (with libsrt, libx264, libx265).
dotnet test tests/DeveLiveStreamer.Streaming.Tests -c Release -p:RestoreLockedMode=true --logger trx --results-directory artifacts/test-results

signing_args=()
if [[ -n "${ANDROID_KEYSTORE_BASE64:-}" ]]; then
    : "${ANDROID_KEYSTORE_PASSWORD:?Release signing requires ANDROID_KEYSTORE_PASSWORD}"
    : "${ANDROID_KEY_ALIAS:?Release signing requires ANDROID_KEY_ALIAS}"
    : "${ANDROID_KEY_PASSWORD:?Release signing requires ANDROID_KEY_PASSWORD}"
    keystore_path="$(mktemp "${TMPDIR:-/tmp}/deve-signing-XXXXXX.keystore")"
    export DEVE_SIGNING_KEYSTORE="$keystore_path"
    python3 -c 'import base64,os,pathlib; pathlib.Path(os.environ["DEVE_SIGNING_KEYSTORE"]).write_bytes(base64.b64decode(os.environ["ANDROID_KEYSTORE_BASE64"]))'
    trap 'python3 -c '\''import os,pathlib; pathlib.Path(os.environ["DEVE_SIGNING_KEYSTORE"]).unlink(missing_ok=True)'\''' EXIT
    signing_label="release"
else
    keystore_path="$project_root/.tools/development.keystore"
    if [[ ! -f "$keystore_path" ]]; then
        keytool -genkeypair -keystore "$keystore_path" -storepass android -alias androiddebugkey -keypass android -dname "CN=DeveLiveStreamer Development,O=Deve,C=NL" -keyalg RSA -keysize 2048 -validity 10000 -noprompt
    fi
    export ANDROID_KEYSTORE_PASSWORD=android ANDROID_KEY_PASSWORD=android ANDROID_KEY_ALIAS=androiddebugkey
    signing_label="development"
fi
signing_args+=("-p:AndroidKeyStore=true" "-p:AndroidSigningKeyStore=$keystore_path"
    "-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD" "-p:AndroidSigningKeyAlias=$ANDROID_KEY_ALIAS"
    "-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD")

# A clean package prevents incremental targets retaining a previous signing identity.
dotnet clean src/DeveLiveStreamer.App -f net10.0-android -c Release
dotnet publish src/DeveLiveStreamer.App -f net10.0-android -c Release -r android-arm64 \
    "-p:AndroidSdkDirectory=$ANDROID_HOME" "-p:JavaSdkDirectory=$JAVA_HOME" \
    "-p:ApplicationVersion=$build_number" "-p:ApplicationDisplayVersion=$build_version" \
    "-p:Version=$build_version" "-p:PublishDir=$output_dir/publish/" -p:RestoreLockedMode=true "${signing_args[@]}"

signed_package="$output_dir/publish/nl.deve.livestreamer-Signed.apk"
[[ -f "$signed_package" ]] || { printf 'Expected signed APK was not produced.\n' >&2; exit 1; }
target="$output_dir/DeveLiveStreamer-$build_version-$signing_label.apk"
cp "$signed_package" "$target"
build_tools="$(find "$ANDROID_HOME/build-tools" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -1)"
"$build_tools/apksigner" verify --verbose --print-certs "$target"
"$build_tools/zipalign" -c -P 16 -v 4 "$target" > "$output_dir/alignment.txt"
python3 "$project_root/eng/verify-apk.py" "$target" "$keystore_path" "$ANDROID_KEY_ALIAS"
printf 'APK ready: %s\n' "$target"
