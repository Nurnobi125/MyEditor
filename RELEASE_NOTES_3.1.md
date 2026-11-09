# MyEditor 3.1.0 — Release notes

## Added
- Live preview now composites still-image and video overlays with animated text. Overlay video preview is muted and uses the same position, opacity and transform animation as export.
- **Cancel export**: the Export button turns into *Cancel export* while rendering; FFmpeg is stopped and the partial file is removed.
- **Time remaining** shown next to export progress.
- **Encoder choice**: H.264 (CPU), H.265 (CPU), and NVIDIA / Intel / AMD GPU H.264. Only encoders present in the installed FFmpeg are enabled. If a GPU encoder fails on the PC, a prompt offers a CPU H.264 retry.
- **Show in folder** prompt after a successful export.
- **Keyboard**: Left/Right frame step, Shift+Left/Right one-second step, Home/End, Ctrl+D duplicate, Ctrl+E export.

## Fixed
- Exporting to the same path as a source file used in the project would overwrite the original media. This is now blocked with an explanation.
- A failed export no longer leaves a broken partial file at the chosen path.

## Remaining limitations
- Preview now composites image/video overlays and animated text. Some clip filters and audio effects are finalized only during export.
- Project files store absolute media paths.
- A single sequential V1 cut track; V2–V4 support independent video/image overlays. Overlay clip audio is not mixed into export.
- The audio of an overlay video clip is not mixed into the export.
- Overlays, text or audio that run past the end of the V1 track are cut off at the end of V1.
- Screen recording is video-only.
- FFmpeg is downloaded on first run without a checksum check.

## Verification
- Source compiles with zero errors against the project's WPF assemblies.
- Export engine tested with real FFmpeg: H.264 and H.265 output (codec, size, duration verified), transitions + animated overlay + extra audio, cancellation (process stopped, partial file deleted), and GPU-encoder failure on a machine without a GPU.
- The WPF window itself has not been run interactively; please click through the new button, encoder box and shortcuts once on Windows before distributing.
