# MyEditor 3.0.0 — Release notes

## Added

- Save/Open for `.myeditor` project files, including timeline clips, overlays, text, keyframes, media-bin references, export profile, zoom and playhead position.
- Automatic 45-second recovery saves, with a restore prompt on startup.
- 170 numbered animation variations covering 17 families for overlay and text clips.
- Text caption/highlight background shapes.
- Built-in CPU-friendly primary-display recording (540p/12fps), with optional pointer tracking and automatic import to the media library, preview and timeline. Recorded clicks appear as draggable ZOOM markers on V2. Select a marker to edit its strength, zoom-in speed and duration; Preview and export share the same settings.
- 540p/24fps low-end CPU export profile with fast H.264 encoding and reduced thread use.
- Self-contained Windows x64 portable ZIP release packaging; users do not need to install .NET.
- Updated to the supported .NET 10 LTS line.

## Known limitations

- Project files reference original media paths; media is not embedded or copied.
- Preview is not a full composite renderer. Overlay video, additional audio and exported effects are rendered by FFmpeg during export.
- AI captions and FFmpeg setup need an internet connection on first use. AI captions then run locally on CPU.
- Screen recording captures video only; microphone and system audio are not captured.
- Export speed depends on CPU, codecs, source resolution and effects. 4K can be slow on low-end systems.
- This release is a portable ZIP, not a signed MSI installer. It is not Authenticode-signed, so Windows SmartScreen may show an unrecognized publisher prompt.

## Release verification

The release is built for Windows x64 with the .NET 10 runtime bundled. Build and package checks are recorded with the release artifact; verify the target PC's media, export and speech-voice workflows before broad distribution.
