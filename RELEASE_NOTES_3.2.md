# MyEditor 3.2.0 — Release notes

## Added
- Three independent video/image overlay lanes (V2–V4), with drag-and-drop moves between lanes and front-to-back ordering in preview and export.
- Optional low-memory preview proxies. Build proxies creates cached 640×360 H.264/AAC preview copies with one CPU thread; the original media remains the export source.
- Low-memory preview preference is stored in the project file.
- Screen-recording playback no longer gets periodically re-seeked from the UI timer; the preview playhead follows the media decoder clock. Mouse-follow camera movement is eased between captured pointer samples.
- Screen recording now offers Lite native-resolution 30fps, Extra Lite 540p/12fps, and native-resolution 60fps modes with constant-frame-rate output pacing.
- Auto/Lite/High performance profiles detect installed RAM; Auto uses Lite below about 7 GiB usable RAM to account for 8 GB-class systems reporting less than 8 GiB, while High selects 1080p30 and an available GPU encoder when one is provided by FFmpeg.
- Mouse movement tracking is denser and steers camera position without creating extra zoom pulses. Click zoom now reaches the configured strength and uses a smooth attack, hold and release envelope; preview pan is clamped to the frame edges like export.
- Selected zoom markers now support Smooth, Cinematic, Snappy and Linear easing curves; the curve is saved with the project and shared by preview/export.
- Export settings now include video quality/file-size balance and AAC audio bitrate, saved per project. Buttons use consistent rounded surfaces and hover/pressed states.
- Timeline clips can be added by dragging media from the library to the desired track, moved with magnetic snapping, and trimmed from visible edge handles. Audio clips support fade-in/out; zoom markers use staggered compact labels to stay easy to select.
- Three audio lanes (A1–A3) can be arranged independently; all lanes are mixed during export. Audio clips can be moved between lanes by dragging.
- Import standard `.srt` subtitle files as editable text clips on T1. Shift+Delete or the timeline context menu ripple-deletes a V1 clip and closes the timeline gap, adjusting overlays and audio that cross or follow the removed section.
- Audio clips receive a cached waveform preview while remaining editable during waveform generation.
- The recorder can capture a user-selected primary-screen rectangle and an optional Windows DirectShow audio input (including Stereo Mix when the device exists). Mouse-follow coordinates are normalized to the selected area.
- High-profile export requests automatic FFmpeg hardware decoding when available. Lite export limits decoder, filter and software encoder threading and runs below normal process priority; available GPU encoders keep the CPU fallback.
- Recorder can capture a chosen open window as well as the full display or selected area; tracked mouse coordinates follow that window's client area.
- Ctrl-click and Ctrl+A select clip groups. Same-type overlay/audio groups move together; V1 groups can be reordered together. Group duplicate, delete and move-to-playhead are supported.

## Remaining limitations
- V1 remains a sequential main cut track; V2–V4 are overlay tracks, and overlay video audio is not mixed into export.
- Filters and audio effects may be finalized only during FFmpeg export.
- Proxy copies use additional disk space under `%LOCALAPPDATA%\MyEditor\proxies` and are keyed to source path, size and modified time.
