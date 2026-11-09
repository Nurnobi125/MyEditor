# Contributing to MyEditor

Thanks for helping improve MyEditor. Contributions are welcome for bug fixes, accessibility, editing and recording workflows, documentation, and build reliability.

## Before you start

1. Search existing issues and pull requests for related work.
2. For a significant feature or behavior change, open an issue first so the approach can be discussed.
3. Keep changes focused and avoid including generated build output, user settings, recordings, or project files.

## Development setup

- Windows 10 or 11, x64.
- .NET 10 SDK and Windows Desktop targeting pack.
- FFmpeg is installed by the application on first launch; it is not checked into this repository.

```powershell
dotnet restore MyEditor.csproj
dotnet build MyEditor.csproj -c Debug
dotnet build MyEditor.csproj -c Release
```

There is no automated test project yet. For changes to editing or media workflows, describe the manual steps and sample media used to check the change. Never commit copyrighted or private media; use a small synthetic sample or a link to freely licensed media instead.

## Pull requests

- Explain the user problem and the behavior changed.
- Link the related issue, if one exists.
- Include build results and manual verification steps.
- Add screenshots or a short screen recording for visible UI changes, using synthetic or freely licensed media.
- Update the README or release notes when behavior or setup instructions change.
- Keep changes compatible with Windows and avoid adding dependencies without explaining why.

By submitting a contribution, you agree that it may be distributed under the repository's MIT License. You retain copyright in your contribution.

## Community

Please follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md).
