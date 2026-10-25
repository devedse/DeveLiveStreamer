# Using DeveLiveStreamer on Android

Install the APK on an ARM64 phone with Android 10 or newer. The first collection contains **Camera**, **Screen share** and **Starting soon** scenes.

## Go live

1. Open **Settings › Stream** (gear icon) and paste your server URL. For DeveStreamingPlatform, use the SRT URL it shows for your stream, for example `srt://your-server:9999?streamid=default/app/my-stream`. The page shows how the URL is understood (host, port and stream ID). RTMP/RTMPS URLs work too; put the key in **Stream key** or at the end of the URL.
2. Pick the scene you want viewers to see and tap **Go live**. Android asks for camera and microphone access, and for screen capture when a scene uses the device screen or device audio is enabled.
3. The status chip shows **LIVE** and the duration; the footer shows bitrate, frame rate and SRT round-trip time. Tap the chip for details: the encoder used, the camera's actual capture mode, settings the phone rejected, and packet loss.
4. Switching scenes while live switches what viewers see. Tap **End stream** to stop. The stream keeps running with the app in the background (a notification shows that you're live).

If the connection drops, the app reconnects automatically and resumes at a fresh key frame.

## Scenes and sources

- The preview is drawn by the same GPU compositor that feeds the encoder, so it shows exactly what is streamed. Sources that have nothing to show yet (no camera permission, no incoming stream) show a labelled box in the preview while offline; it is never streamed.
- Tap a source in the preview to select it, drag to move (the page no longer scrolls while dragging), and drag the bottom-right handle to resize. Visibility, lock and order are in the source list, which also shows each source's live status.
- **Camera**: choose the device (front, rear or a specific lens) in Properties. Several sources can use the same camera; different cameras at the same time work only if the phone supports it, otherwise the source says so. **Mirror** flips the image (front cameras arrive mirrored like a selfie).
- **Device screen**: captures the whole screen while live.
- **Network stream**: an incoming RTMP or SRT stream, e.g. from a drone, another phone or OBS.
  - Receive on this phone: `rtmp://0.0.0.0:1935/live/stream` or `srt://0.0.0.0:9000`, then push to `rtmp://<phone-ip>:1935/live/stream` or `srt://<phone-ip>:9000`. Properties lists the phone's addresses. With an RTMP path set, only that app/key is accepted.
  - Pull from a server: `rtmp://server/app/key` or `srt://server:port?streamid=…`.
  - Its audio is mixed into the stream unless **Include this stream's audio** is off.
- **Text**, **Image** and **Color** layers render at the output resolution.

## Output

Video settings accept any resolution and rational frame rate (presets are shortcuts). The encoder applies what the phone supports; anything it rejects (for example CBR or a profile) is listed in the stream details instead of being silently changed. Frame rates above the camera's normal range use a constrained high-speed camera session when the phone offers one.
