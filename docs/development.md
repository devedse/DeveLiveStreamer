# Development and builds

## Current scope

Version 0.2.1 includes the Android streaming implementation and a fix for lost encoder-format events during startup. Scenes from the shared C# model are composed on the GPU on Android, encoded with the phone's hardware H.264/HEVC encoder and AAC, and published over SRT (MPEG-TS) or RTMP/RTMPS (FLV, enhanced RTMP for HEVC). Sources: cameras (Camera2, including constrained high-speed sessions), the device screen (MediaProjection), incoming RTMP/SRT streams (pulled or received by listening on the phone), images, text and colors. Microphone, device audio (playback capture) and network-source audio are mixed. See the validation section for tested paths and remaining device checks.

iOS shares the model and editor UI but has no capture/streaming engine yet; it builds only on macOS and has not been built here. There is no ReplayKit extension.

## Architecture

| Path | Responsibility |
|---|---|
| `src/DeveLiveStreamer.Core` | Versioned scene collection, reusable sources, per-scene placement, output settings, edit history, atomic storage |
| `src/DeveLiveStreamer.Streaming` | Platform-independent media plumbing: H.264/HEVC/AAC parameter handling, MPEG-TS mux/demux, FLV/AMF0, RTMP client and ingest server, libsrt P/Invoke, reconnecting `LivePublisher`, `IncomingStream` (RTMP/SRT pull or listen), PCM `AudioMixer` |
| `src/DeveLiveStreamer.App/Studio` | Engine contract (`IStudioEngine`), immutable `RenderScene` snapshots, layer geometry (crop/fit, mirroring, camera rotation) |
| `src/DeveLiveStreamer.App/Platforms/Android/Studio` | Android engine: EGL/GLES compositor thread, camera/screen/network sources, MediaCodec encoders and decoders, audio capture, foreground service, preview `TextureView` |
| `tests/DeveLiveStreamer.Core.Tests` | Scene model, storage and layer-geometry tests |
| `tests/DeveLiveStreamer.Streaming.Tests` | Format round trips plus SRT/RTMP interoperability with FFmpeg |

The UI hands the engine a `RenderScene` snapshot after every edit (including each drag step). The compositor thread renders the visible layers bottom-to-top into an output-sized framebuffer on its own clock at the output frame rate; that frame is drawn into the encoder's input surface with its presentation time and blitted, letterboxed, into the preview. The display refresh never paces the output (the preview swap interval is 0). When nothing is encoding, the framebuffer is sized to the preview to save power.

Camera frames arrive through `SurfaceTexture`, whose transform already rotates the sensor image to the device's natural orientation; the compositor only compensates the display rotation and center-crops into the layer (`LayerGeometry`). Sources are opened on demand for the program scene and released shortly after they leave it. One `CameraSource` exists per camera device, shared by every layer that uses it.

Encoded frames go to `LivePublisher`, which connects once the encoder's parameter sets are known, starts every connection at a key frame, drops video back to the next key frame (and requests one) if frames wait longer than two seconds, and reconnects with back-off. For DeveStreamingPlatform the SRT URL's `streamid` (e.g. `default/app/name`) is passed unchanged to OvenMediaEngine; other query options do not leak into it. SRT latency is set in milliseconds from the settings, and zero is allowed.

### Native SRT library

Android uses `libsrtdroid.so` from the published `srtdroid-core` 1.10.1 AAR (libsrt 1.5.7 statically linked with OpenSSL 3.5.1), whose exported `srt_*` C API is called directly through P/Invoke. An MSBuild target downloads the AAR into `.tools/native/`, verifies its SHA-256 and extracts the ARM64/x86-64 libraries. Desktop tests use the system libsrt 1.5 (`libsrt-gnutls.so.1.5` on Debian/Ubuntu).

## Toolchain

The checked-in SDK selector pins .NET **10.0.402** with roll-forward disabled so local builds and CI use the same SDK and implicit linker dependencies. This runner installed the SDK, the `maui-android` workload from its manifests, Microsoft JDK **21.0.8**, Android platform **36** and build tools **36.0.0**. MAUI packages are explicitly pinned to **10.0.120**; NuGet lock files are included for the app and tests. The dependency-free core library does not generate a lock file, so desktop tests and Android runtime restore do not overwrite the same lock graph. Update these as a tested set.

`eng/setup-android.sh` bootstraps Linux x86-64 and generates an ignored `.tools/env.sh`. Pass a tool directory explicitly to place downloads and workloads on a suitable volume. The current runner uses `/tmp/deve-live-streamer-tools`; those tools must be recreated if that temporary volume is cleared. The source and build scripts do not depend on retaining it.

PowerShell 7.4+ is the build entry point, matching [DeveMobileLPR](https://github.com/devedse/DeveMobileLPR/blob/master/build.ps1). Run `./build.ps1 -Configuration Release` on a configured environment. It checks the workload, restores the solution in locked mode, builds without restoring again, tests both desktop projects without rebuilding and collects TRX/coverage results. It then calls `eng/Publish-Android.ps1`. `-SkipPublish` runs through the tests only; `-Version`, `-ApplicationVersion` and `-ApplicationDisplayVersion` set assembly, numeric Android and display versions. Defaults come from `BUILD_VERSION` / `BUILD_NUMBER` or the app project.

`eng/build-android.sh` loads `.tools/env.sh` and forwards arguments to that same entry point. The Linux bootstrap installs a pinned PowerShell if needed; ordinary builds never install prerequisites. Set `JAVA_HOME/bin` on `PATH` so `keytool` is available. Packaging can also be invoked directly with `pwsh -NoProfile -File eng/Publish-Android.ps1 -Configuration Release`.

When using a preinstalled environment:

```bash
export DOTNET_ROOT=/path/to/dotnet
export JAVA_HOME=/path/to/jdk-21
export ANDROID_HOME=/path/to/android-sdk
export PATH="$DOTNET_ROOT:$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$PATH"
dotnet workload install maui-android --skip-manifest-update
./eng/build-android.sh
```

For the configured runner:

```bash
source .tools/env.sh
dotnet test tests/DeveLiveStreamer.Core.Tests -c Release
./eng/build-android.sh
adb install -r artifacts/android/DeveLiveStreamer-0.2.1-development.apk
```

The app minimum is Android API 29. The universal APK includes `arm64-v8a` and `x86_64`; 32-bit ARM is not packaged in this first build. Camera access is requested when a scene uses a camera; microphone, notification and screen-capture consent when going live. Image selection uses the system picker.

## Android layout compatibility

The pinned MAUI version's batched view initialization converts unset (`NaN`) minimum dimensions directly to native integers. On this Android runtime that produced `2147483647` pixel minimums and an unusable layout. `AndroidLayoutCompatibility` runs MAUI's normal minimum-width/height mapping after the batch, resolving unset values to zero while preserving explicit minimums. Keep this guard until a framework update is validated without it. It affects UI measurement only.

Upstream implementation: [batched handler initialization](https://github.com/dotnet/maui/blob/10.0.120/src/Core/src/Handlers/View/ViewHandler.cs), [native view initialization and minimum mapping](https://github.com/dotnet/maui/blob/10.0.120/src/Core/src/Platform/Android/ViewExtensions.cs).

## Signing and release behavior

Without release secrets, the build script generates/reuses `.tools/development.keystore`. This is a development signing identity with the standard development password; the private file is ignored and must not be committed. Subsequent local builds reuse it so the APK can update an existing local installation.

For release signing, set these through the build environment or GitHub repository secrets:

- `ANDROID_KEYSTORE_BASE64`
- `ANDROID_KEYSTORE_PASSWORD`
- `ANDROID_KEY_ALIAS`
- `ANDROID_KEY_PASSWORD`

The keystore is decoded to a temporary file and removed when packaging exits. Store/key passwords are supplied through MSBuild environment references. Alternatively, pass `-Keystore`, `-KeystorePassword`, `-KeyAlias` and `-KeyPassword` directly to `eng/Publish-Android.ps1`. An incomplete local signing configuration fails. The published APK is checked against the selected signing certificate and requested versions. Old signed intermediate APKs are removed before publishing, forcing signing to run again when switching identities. An install already signed with a different development/release identity cannot be updated in place; export the scene collection before changing signing channels.

The workflow uses DeveMobileLPR's `onyxmueller/build-tag-number` counter and `1.0.<build number>` versioning. The generated number sets Android's numeric version, and the full version is used for assemblies, display, artifacts and release tags. Pull-request builds use the workflow run number without writing counter tags. All four repository secrets must be configured to enable release signing. The release job runs only for a successful, release-signed master push. APKs are uploaded on every successful build; TRX/coverage results are uploaded even if a later step fails. Verification metadata stays in the build workspace; downloadable release assets contain APKs only.

The Android CI job runs on Ubuntu to retain real FFmpeg/libsrt interoperability tests. The app retains its validated Mono runtime and ARM64/x86-64 packaging. PowerShell packaging also supports Windows SDK tool names. The pinned .NET SDK and native library checks remain part of the build. The earlier shell-based workflow completed successfully before this migration.

The PowerShell flow was validated locally with both development signing and a separate release key: all 52 reported test cases passed with coverage, both APKs passed package verification, and switching the signing identity forced a new signature. Incomplete signing configuration was rejected before creating output. The app disables .NET's default desktop publish-RID inference so solution builds retain only the configured Android runtime identifiers.

Builds clear stale TRX/coverage results and packaging clears old APKs and their verification reports. A partially configured CI signing identity fails explicitly; pull requests receive no signing secrets. Screen/camera consent is derived from `RenderScene` (the active scene's visible, nontransparent layers). `CaptureConsentState` prevents repeated requests after denial until the source is reactivated. Live scene switches can acquire screen capture without restarting the encoders or publisher. The live session owns the projection callback, including audio-only capture; system revocation releases capture resources. A consent token creates at most one virtual display, as required by Android 14+. Device-audio-only startup does not create a video display.

## Saved data

The app stores `scene-collection.json` in its private application-data directory. A save validates the document, writes a temporary file and replaces the current file. The previous valid save is retained as `.bak`; recovery avoids replacing that backup with an invalid latest file. Unknown document versions or invalid source references are rejected. JSON serialization uses generated metadata so it survives Release trimming.

Images are copied into the app's private `images/` directory. JSON export includes source configuration and local image paths, not image bytes. Export/import is useful for inspection and same-device backup; moving a collection to a different device requires re-adding its image assets. Server URLs in exported settings are part of the JSON, so export only configuration you intend to share.

## Validation

`build.ps1` (also called by `eng/build-android.sh`) runs both test projects before packaging (58 regular tests plus an opt-in server test; 59 cases reported by the test runner):

- **Core (32):** scene references, layer order, history, storage recovery, uncapped output settings, codec-imposed constraints (even 4:2:0 sizes, AAC sample rates), active-scene capture consent (including hidden/transparent sources, device audio, denial, revocation and reactivation) and the layer geometry used by the compositor: center-crop, letterbox, mirroring, all four display rotations and the GL matrix layout.
- **Streaming (27):** NAL/AVC/HEVC/AAC/AMF0 round trips, MPEG-TS and FLV round trips, DeveStreamingPlatform URL parsing (OME `streamid` routes, encoded IDs, IPv6, passphrases), the mixer, publisher reconnection/key-frame/video-only behavior, and interoperability with FFmpeg 7.1 over loopback:
  - our MPEG-TS (H.264 and HEVC) decodes in FFmpeg without errors;
  - our SRT publisher delivers every frame to an FFmpeg SRT listener, for H.264 and HEVC;
  - our RTMP publisher delivers H.264 and enhanced-RTMP HEVC to an FFmpeg RTMP server;
  - FFmpeg can push RTMP and SRT into the app's ingest sources, a wrong RTMP key is rejected, and RTMP pull works;
  - the SRT listener sees the publisher's stream ID.

The opt-in `PublishesInRealTimeToAConfiguredServer` test takes `DLS_PUBLISH_URL` and optional `DLS_PUBLISH_SECONDS`. A recorded desktop run against DSP sent 600 H.264 frames in 20 seconds with no reported drops or reconnects. That verifies transport from the desktop, not Android capture or DSP playback.

Version 0.2.1 corrects an Android startup race: the encoders previously began delivering output from their constructors, before the publisher subscribed to format events. A video format emitted during audio initialization could be lost permanently, leaving the publisher waiting at “Starting encoders…” even while the codec returned frames. Output workers now start explicitly after all consumers subscribe; codec initialization runs off the UI thread. Stream details include input/output counters for both encoders. Encoder tuning fallback and timestamp rebasing are additional recovery measures, not established explanations for a particular phone's failure.

A 0.2.1 emulator run with a 320×180, 15 fps color/text scene delivered both format events to the publisher. Initial connection attempts failed because the emulator was disconnected from Wi-Fi. After a clean restart, `adb shell cmd wifi connect-network AndroidWifi open` restored connectivity; check `adb shell cmd wifi status` and ping the host before testing publishing.

With connectivity established, FFmpeg received the Android app's SRT stream and recorded 59 H.264 video frames plus 63 AAC frames. The recording decodes with `ffmpeg -v error -xerror -i android-capture.ts -f null -` without errors, and an extracted frame shows the composed text and color layers in the correct orientation. Artifacts are retained locally under `artifacts/validation-0.2.1/local/`. The receiver deliberately stops after 12 seconds, after which the app reports a send error and reconnects. The recording has frame/audio gaps under software emulation; this is a functional transport check, not a frame-rate or audio-continuity benchmark. Its small output size is a validation fixture, not an application limit.

A subsequent direct Android-to-DSP run used a lighter 320×180, 5 fps scene with 16 kHz mono AAC to keep the software emulator responsive. The app stayed Live for more than 55 seconds, reporting nonzero bitrate and roughly 3–5 ms SRT RTT. A packet capture over approximately 55 seconds recorded 1,204 outgoing SRT data packets containing 3,498 MPEG-TS packets (including both video PID `0x100` and audio PID `0x101`) and 1,069 acknowledgements from DSP. This confirms Android transmission and acknowledgement by DSP; it does not verify player playback. The capture summary is retained under `artifacts/validation-0.2.1/dsp-wire-summary.json`.

On the local Android 11 x86-64 emulator (software CPU, GPU and codecs, no KVM), the APK installs and starts; the compositor renders the preview, permissions and screen-capture consent work, and the foreground service and MediaProjection start. The software AAC encoder hit Codec2 watchdog failures in earlier runs; the app can recreate it and fall back to video-only if no audio format arrives. **DSP player playback and physical-phone behavior still need confirmation**, including camera orientation, concurrent cameras, high-speed modes, device-audio capture and sustained high-resolution/high-frame-rate output. The emulator's Android framework also became unresponsive and restarted during heavier tests; these runs are not a stability or performance sign-off.

The APK verifier checks ZIP integrity, application ID and minimum API, ARM64/x86-64 contents including `libsrtdroid.so`, 16 KB ELF alignment, the signing certificate and the checksum.
