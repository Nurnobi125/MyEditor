# MyEditor Video Studio

**Open source Windows video editor and screen recorder** built with C#, .NET 10, and WPF. Edit video on a multitrack timeline, record your screen, add animated titles and overlays, and export H.264 or H.265 video.

MyEditor is designed for Windows and keeps editing local. Project files store media paths and editing choices; they do not upload your footage.

## Features

- Multitrack timeline for video, overlays, titles, stickers, and audio.
- Screen recording with microphone and optional system audio, webcam picture-in-picture, cursor capture, and pause/resume.
- Animated titles, captions, transitions, shapes, stickers, templates, and backgrounds.
- H.264/H.265 export with CPU encoding and supported GPU encoders.
- Project save, autosave, and recovery.
- Optional local speech-to-text captions with whisper.cpp.
- Optional preview proxies for lower-memory computers.

## Build from source

### Requirements

- Windows 10 or 11, x64.
- .NET 10 SDK with the Windows Desktop workload/targeting pack.
- Visual Studio 2022 or a compatible editor is optional; the .NET CLI is sufficient.

```powershell
git clone https://github.com/<your-account>/MyEditor.git
cd MyEditor
dotnet restore MyEditor.csproj
dotnet build MyEditor.csproj -c Release
```

Run the app from Visual Studio, or from the build output under `bin/Release/net10.0-windows/`.

On first launch, MyEditor downloads FFmpeg to the current user's local application data folder. An internet connection is needed for this setup. Local captions download whisper.cpp and its model only when that feature is selected. Neither runtime is included in this source repository; see [third-party notices](THIRD_PARTY_NOTICES.txt).

## Try a release

When a Windows release is available, download it from the repository's **Releases** page. Until then, build from source using the steps above. MyEditor is currently a Windows desktop application and does not support macOS or Linux.

## Contributing

Bug reports, feature ideas, documentation fixes, and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Please do not attach private projects, recordings, or personal media to public issues.

## Project files and privacy

`.myeditor` project files contain edit decisions and paths to source media. The app does not copy source media into the project file. Keep source files available when reopening a project.

## License

The MyEditor source code is licensed under the MIT License; see [LICENSE](LICENSE). External tools and packages have their own licenses. In particular, FFmpeg is downloaded separately and its distribution terms depend on the build used. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

## Search description

GitHub repository description: **Open-source Windows video editor and screen recorder with a multitrack timeline, animated titles, effects, captions, webcam, audio mixing, and H.264 export.**

Suggested GitHub topics: `windows`, `video-editor`, `screen-recorder`, `video-editing`, `timeline-editor`, `wpf`, `dotnet`, `csharp`, `ffmpeg`, `h264`, `captions`, `open-source`.
