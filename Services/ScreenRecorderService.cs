using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using VideoEditor.Models;

namespace VideoEditor.Services;

/// <summary>Lists the DirectShow inputs FFmpeg can see.</summary>
public sealed record RecordingDevices(IReadOnlyList<string> Audio, IReadOnlyList<string> Video);

/// <summary>
/// Screen recorder built on FFmpeg: full screen / area / window capture, NVENC-QSV-AMF hardware encoding with
/// automatic CPU fallback, microphone + system-audio mixing, webcam picture-in-picture, crash-safe fragmented
/// segments with pause/resume, live size / dropped-frame statistics and optional pointer sampling.
/// </summary>
public sealed class ScreenRecorderService : IDisposable
{
    public static bool IsCaptureWindowAvailable(CaptureWindowInfo? window) =>
        window != null && TryGetClientBounds(window.Handle, out _, out _, out _, out _);

    const int WhMouseLl = 14;
    const int WhKeyboardLl = 13;
    const int WmMouseMove = 0x0200;
    const int WmKeyDown = 0x0100;
    const int WmSysKeyDown = 0x0104;
    const int WmLButtonDown = 0x0201;
    const int WmRButtonDown = 0x0204;
    const int WmMButtonDown = 0x0207;
    const int WmXButtonDown = 0x020B;
    const int MaxEvents = 50000;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static HashSet<string>? _hardwareEncoders;

    readonly List<PointerEvent> _events = new();
    readonly List<string> _segments = new();
    readonly List<string> _loopbackSegments = new();
    readonly Stopwatch _segmentClock = new();
    readonly MouseProc _mouseProc;
    readonly KeyboardProc _keyboardProc;

    RecorderOptions _o = new();
    Process? _process;
    IntPtr _hook, _keyboardHook;
    readonly object _errorSync = new();
    readonly Queue<string> _errorLines = new();
    string? _outputPath;
    string? _segmentDirectory;
    string _encoderName = "libx264";
    bool _trackMouse, _paused, _forceCpu;
    bool _captureRecovered;
    string _smartAssistantStatus = "";
    int _screenWidth, _screenHeight, _lastX, _lastY, _segmentNumber, _movesRecorded;
    int _captureFps = 30;
    int _targetHeight;
    int _captureX, _captureY, _captureWidth, _captureHeight;
    int _droppedBase, _liveDropped;
    long _finishedBytes, _liveBytes;
    double _elapsedBeforeSegment, _lastMoveAt = -1, _lastTypingAt = -1, _speed;
    CaptureArea? _captureArea;
    CaptureWindowInfo? _captureWindow;
    WasapiLoopbackCapture? _loopbackCapture;

    // ---------------------------------------------------------------- state / stats
    public bool IsRecording => !_paused && _process is { HasExited: false };
    public bool IsPaused => _paused;
    /// <summary>True when FFmpeg stopped by itself while we were still recording (device unplugged, window closed...).</summary>
    public bool CaptureEnded => !_paused && _process is { HasExited: true };
    public double ElapsedSeconds => _elapsedBeforeSegment + _segmentClock.Elapsed.TotalSeconds;
    public int MaxSeconds => _o.MaxMinutes > 0 ? _o.MaxMinutes * 60 : 0;
    public bool LimitReached => MaxSeconds > 0 && ElapsedSeconds >= MaxSeconds;
    public long FileSizeBytes => Interlocked.Read(ref _finishedBytes) + Interlocked.Read(ref _liveBytes);
    public int DroppedFrames => _droppedBase + _liveDropped;
    /// <summary>Encoding speed reported by FFmpeg (1.0 = real time). Below ~0.95 the PC can't keep up.</summary>
    public double EncodeSpeed => _speed;
    public string EncoderLabel => _encoderName switch
    {
        "h264_nvenc" => "NVIDIA NVENC",
        "h264_qsv" => "Intel Quick Sync",
        "h264_amf" => "AMD AMF",
        "h264_mf" => "Windows Media Foundation",
        _ => "CPU x264"
    };
    public string? LastError { get { lock (_errorSync) return _errorLines.Count == 0 ? null : string.Join(Environment.NewLine, _errorLines); } }
    public IReadOnlyList<PointerEvent> PointerEvents => _events;

    public string RecordingDescription
    {
        get
        {
            string size = _targetHeight > 0 ? Math.Min(_targetHeight, _captureHeight) + "p" : $"{_captureWidth}×{_captureHeight}";
            string source = _captureWindow != null ? "window: " + _captureWindow.Title : _captureArea == null ? "full screen" : "selected area";
            var parts = new List<string> { size, _captureFps + "fps", EncoderLabel, source };
            bool mic = !string.IsNullOrEmpty(_o.MicDevice), sys = !string.IsNullOrEmpty(_o.SystemAudioDevice);
            parts.Add(mic && sys ? "mic + system audio" : mic ? "mic" : sys ? "system audio" : "no audio");
            if (!string.IsNullOrEmpty(_o.WebcamDevice)) parts.Add("webcam");
            if (_smartAssistantStatus.Length > 0) parts.Add(_smartAssistantStatus);
            return string.Join(" · ", parts);
        }
    }

    public ScreenRecorderService() { _mouseProc = MouseHookCallback; _keyboardProc = KeyboardHookCallback; }

    // ---------------------------------------------------------------- start / pause / resume / stop
    /// <summary>Compatibility overload used by older callers.</summary>
    public Task StartAsync(string outputPath, bool trackMouse, int captureFps = 12, bool nativeResolution = false,
        string? audioDevice = null, CaptureArea? captureArea = null, CaptureWindowInfo? captureWindow = null)
        => StartAsync(outputPath, new RecorderOptions
        {
            TrackMouse = trackMouse, Fps = captureFps, TargetHeight = nativeResolution ? 0 : 540,
            MicDevice = audioDevice, Area = captureArea, Window = captureWindow
        });

    public async Task StartAsync(string outputPath, RecorderOptions options)
    {
        if (_process != null || _paused) throw new InvalidOperationException("A screen recording is already running.");
        _o = options.Clone();
        _events.Clear(); _segments.Clear(); _loopbackSegments.Clear(); lock (_errorSync) _errorLines.Clear(); _movesRecorded = 0; _lastMoveAt = -1; _lastTypingAt = -1;
        _elapsedBeforeSegment = 0; _segmentNumber = 0; _trackMouse = _o.TrackMouse; _paused = false; _captureRecovered = false;
        _finishedBytes = 0; _liveBytes = 0; _droppedBase = 0; _liveDropped = 0; _speed = 0; _forceCpu = false;
        _outputPath = outputPath;
        _captureFps = Math.Clamp(_o.Fps, 10, 60);
        _targetHeight = Math.Max(0, _o.TargetHeight);
        _smartAssistantStatus = "";
        if (_o.SmartAssistEnabled)
        {
            var advice = LocalRecordingAssistant.Recommend(_captureFps, _targetHeight, _o.Quality, _o.SystemRamGiB, Environment.ProcessorCount);
            _captureFps = advice.Fps; _targetHeight = advice.TargetHeight; _o.Quality = advice.Quality;
            _smartAssistantStatus = advice.Summary;
        }
        _o.MicDevice = string.IsNullOrWhiteSpace(_o.MicDevice) ? null : _o.MicDevice.Trim();
        if (string.Equals(_o.SystemAudioDevice, _o.MicDevice, StringComparison.OrdinalIgnoreCase)) _o.SystemAudioDevice = "";

        _screenWidth = GetSystemMetrics(0); _screenHeight = GetSystemMetrics(1);
        if (_screenWidth < 2 || _screenHeight < 2) throw new InvalidOperationException("Could not detect the primary display size.");
        _captureWindow = _o.Window;
        _captureArea = _captureWindow == null ? _o.Area : null;
        if (_captureWindow != null)
        {
            if (!TryGetClientBounds(_captureWindow.Handle, out _captureX, out _captureY, out _captureWidth, out _captureHeight))
                throw new InvalidOperationException("That window is no longer available. Choose it again and keep it open.");
        }
        else
        {
            _captureX = Math.Clamp(_captureArea?.X ?? 0, 0, _screenWidth - 1);
            _captureY = Math.Clamp(_captureArea?.Y ?? 0, 0, _screenHeight - 1);
            _captureWidth = Math.Clamp(_captureArea?.Width ?? _screenWidth, 2, _screenWidth - _captureX);
            _captureHeight = Math.Clamp(_captureArea?.Height ?? _screenHeight, 2, _screenHeight - _captureY);
        }

        _segmentDirectory = Path.Combine(Path.GetTempPath(), "MyEditor", "recording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_segmentDirectory);
        try
        {
            await StartSegmentAsync();
            if (_trackMouse) InstallHook();
        }
        catch
        {
            Unhook();
            await AbortActiveSegmentAsync();
            _segmentClock.Stop();
            throw;
        }
    }

    public async Task PauseAsync()
    {
        if (!IsRecording) return;
        Unhook();
        await FinalizeActiveSegmentAsync();
        _elapsedBeforeSegment += _segmentClock.Elapsed.TotalSeconds;
        _segmentClock.Reset();
        _paused = true;
    }

    public async Task ResumeAsync()
    {
        if (!_paused) return;
        try
        {
            await StartSegmentAsync();
            if (_trackMouse) InstallHook();
        }
        catch
        {
            Unhook();
            await AbortActiveSegmentAsync();
            _segmentClock.Reset();
            throw;
        }
        _paused = false;
    }

    public async Task<bool> RecoverUnexpectedCaptureStopAsync()
    {
        if (!CaptureEnded || _captureRecovered) return false;
        _captureRecovered = true;
        bool useLoopback = RecorderAudioSource.IsWasapiLoopback(_o.SystemAudioDevice);
        try { await FinalizeActiveSegmentAsync(); } catch { }
        if (useLoopback) await DiscardLoopbackSegmentAsync();
        _elapsedBeforeSegment += _segmentClock.Elapsed.TotalSeconds;
        _segmentClock.Reset();
        _captureWindow = null; _captureArea = null;
        _captureX = 0; _captureY = 0; _captureWidth = _screenWidth; _captureHeight = _screenHeight;
        _forceCpu = true; _encoderName = "libx264";
        _captureFps = Math.Min(_captureFps, 12);
        _targetHeight = _targetHeight == 0 ? 540 : Math.Min(_targetHeight, 540);
        _o.Quality = RecorderQuality.Small;
        _o.WebcamDevice = ""; _o.MicDevice = null;
        if (!useLoopback) _o.SystemAudioDevice = "";
        _smartAssistantStatus = "Recovered after capture interruption · low-load full screen";
        try
        {
            await StartSegmentAsync();
            if (_trackMouse) InstallHook();
            return true;
        }
        catch (Exception ex)
        {
            AddError("Automatic recovery failed: " + ex.Message);
            return false;
        }
    }

    public async Task<IReadOnlyList<PointerEvent>> StopAsync()
    {
        Unhook();
        if (_process != null && !_paused)
        {
            await FinalizeActiveSegmentAsync();
            _elapsedBeforeSegment += _segmentClock.Elapsed.TotalSeconds;
            _segmentClock.Stop();
        }
        _paused = false;
        if (_segments.Count == 0)
            throw new InvalidOperationException("No usable screen frames were captured. " + (LastError ?? "Check that Windows is unlocked and the capture source is visible.") +
                (_segmentDirectory != null ? "\n\nTemporary recording parts are kept here: " + _segmentDirectory : ""));
        try { await JoinSegmentsAsync(); }
        catch (Exception ex)
        {
            // Keep the raw segments: they are valid MP4 files and can still be recovered by hand.
            throw new InvalidOperationException(ex.Message + "\nYour raw recording parts were kept in: " + _segmentDirectory);
        }
        try { if (_segmentDirectory != null && Directory.Exists(_segmentDirectory)) Directory.Delete(_segmentDirectory, true); } catch { }
        return _events.ToArray();
    }

    // ---------------------------------------------------------------- encoders / devices
    /// <summary>Hardware H.264 encoders present in the bundled FFmpeg build (cached).</summary>
    public static async Task<HashSet<string>> DetectHardwareEncodersAsync()
    {
        if (_hardwareEncoders != null) return _hardwareEncoders;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string text = await RunFfmpegForTextAsync("-hide_banner", "-encoders");
            foreach (Match m in Regex.Matches(text, @"\b(h264_nvenc|h264_qsv|h264_amf|h264_mf)\b")) found.Add(m.Value);
        }
        catch { /* FFmpeg missing or not runnable: CPU encoding only */ }
        return _hardwareEncoders = found;
    }

    public static async Task<RecordingDevices> ListDevicesAsync()
    {
        string text = await RunFfmpegForTextAsync("-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
        var audio = new List<string>();
        var video = new List<string>();
        foreach (Match m in Regex.Matches(text, "\"(?<name>[^\"]+)\"\\s+\\((?<kind>audio|video)\\)", RegexOptions.IgnoreCase))
        {
            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;
            var list = m.Groups["kind"].Value.Equals("audio", StringComparison.OrdinalIgnoreCase) ? audio : video;
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }
        return new RecordingDevices(audio, video);
    }

    public static async Task<IReadOnlyList<string>> ListAudioDevicesAsync() => (await ListDevicesAsync()).Audio;

    static async Task<string> RunFfmpegForTextAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await errorTask) + "\n" + (await outputTask);
    }

    async Task<string> ResolveEncoderAsync()
    {
        if (_forceCpu || _o.Encoder == RecorderEncoder.Cpu) return "libx264";
        var hw = await DetectHardwareEncodersAsync();
        string wanted = _o.Encoder switch
        {
            RecorderEncoder.Nvidia => "h264_nvenc",
            RecorderEncoder.Intel => "h264_qsv",
            RecorderEncoder.Amd => "h264_amf",
            RecorderEncoder.MediaFoundation => "h264_mf",
            _ => ""
        };
        if (wanted.Length > 0) return hw.Contains(wanted) ? wanted : "libx264";
        foreach (var candidate in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_mf" })
            if (hw.Contains(candidate)) return candidate;
        return "libx264";
    }

    // ---------------------------------------------------------------- FFmpeg process
    async Task StartSegmentAsync()
    {
        _encoderName = await ResolveEncoderAsync();
        bool useLoopback = RecorderAudioSource.IsWasapiLoopback(_o.SystemAudioDevice);
        StartSegmentProcess();
        if (useLoopback) await StartLoopbackSegmentAsync();
        await Task.Delay(_encoderName == "libx264" ? 1200 : 1600);
        if (_process is not { HasExited: true }) return;

        string originalError = LastError ?? "FFmpeg could not open the screen or one of the selected recording devices.";
        await DisposeFailedSegmentAsync(useLoopback);
        if (_encoderName != "libx264")
        {
            _forceCpu = true; _encoderName = "libx264";
            StartSegmentProcess();
            if (useLoopback) await StartLoopbackSegmentAsync();
            await Task.Delay(1200);
            if (_process is not { HasExited: true }) return;
            originalError = LastError ?? originalError;
            await DisposeFailedSegmentAsync(useLoopback);
        }

        // A stale/minimized window, invalid area, camera or DirectShow device can kill gdigrab/FFmpeg at startup.
        // Retry one time with a safe full-screen, CPU-only profile so users can still capture the session.
        bool changedSource = _captureWindow != null || _captureArea != null;
        _captureWindow = null; _captureArea = null;
        _captureX = 0; _captureY = 0; _captureWidth = _screenWidth; _captureHeight = _screenHeight;
        _forceCpu = true; _encoderName = "libx264";
        _captureFps = Math.Min(_captureFps, 12);
        _targetHeight = _targetHeight == 0 ? 540 : Math.Min(_targetHeight, 540);
        _o.Quality = RecorderQuality.Small;
        bool removedCamera = !string.IsNullOrWhiteSpace(_o.WebcamDevice);
        bool removedMic = !string.IsNullOrWhiteSpace(_o.MicDevice) || (!string.IsNullOrWhiteSpace(_o.SystemAudioDevice) && !useLoopback);
        _o.WebcamDevice = ""; _o.MicDevice = null;
        if (!useLoopback) _o.SystemAudioDevice = "";
        StartSegmentProcess();
        if (useLoopback) await StartLoopbackSegmentAsync();
        await Task.Delay(1600);
        if (_process is { HasExited: true })
        {
            string detail = LastError ?? originalError;
            await DisposeFailedSegmentAsync(useLoopback);
            string advice = changedSource ? " The selected window or area was unavailable, so MyEditor also tried the full screen." : "";
            throw new InvalidOperationException("Screen recording could not open the capture input after automatic CPU and low-resolution recovery." + advice + "\n\nCheck that the desktop is unlocked and close other screen-capture apps.\n\nDetails: " + detail);
        }
        _smartAssistantStatus = "Recovered · full screen, low-load profile" + (removedCamera || removedMic ? " · optional camera/audio disabled" : "");
    }

    async Task DisposeFailedSegmentAsync(bool useLoopback)
    {
        if (useLoopback) await DiscardLoopbackSegmentAsync();
        var failed = _process; _process = null;
        if (failed == null) return;
        try { if (!failed.HasExited) failed.Kill(true); } catch { }
        try { await failed.WaitForExitAsync(); } catch { }
        try { failed.Dispose(); } catch { }
    }

    async Task DiscardLoopbackSegmentAsync()
    {
        string? last = _loopbackSegments.LastOrDefault();
        await StopLoopbackSegmentAsync();
        if (last == null) return;
        _loopbackSegments.Remove(last);
        try { File.Delete(last); } catch { }
    }

    async Task StartLoopbackSegmentAsync()
    {
        if (_segmentDirectory == null) throw new InvalidOperationException("Recording workspace is not ready.");
        string path = Path.Combine(_segmentDirectory, $"system-audio-{_loopbackSegments.Count:0000}.wav");
        var capture = new WasapiLoopbackCapture();
        try
        {
            await capture.StartAsync(path);
            _loopbackCapture = capture;
            _loopbackSegments.Add(path);
        }
        catch (Exception ex)
        {
            try { capture.Dispose(); } catch { }
            throw new InvalidOperationException("WASAPI could not open the Windows playback device. Check that an output device is connected, then try again.", ex);
        }
    }

    async Task StopLoopbackSegmentAsync()
    {
        var capture = _loopbackCapture;
        if (capture == null) return;
        try
        {
            await capture.StopAsync();
        }
        catch (Exception ex) { AddError("System audio capture stopped early: " + ex.Message); }
        finally
        {
            try { capture.Dispose(); } catch { }
            _loopbackCapture = null;
        }
    }

    string CurrentSegmentPath => Path.Combine(_segmentDirectory!, $"part-{_segmentNumber - 1:0000}.mp4");

    void StartSegmentProcess()
    {
        string segmentPath = Path.Combine(_segmentDirectory!, $"part-{_segmentNumber++:0000}.mp4");
        try { if (File.Exists(segmentPath)) File.Delete(segmentPath); } catch { }
        _liveBytes = 0; _liveDropped = 0; _speed = 0;

        var start = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in BuildArguments(segmentPath)) start.ArgumentList.Add(arg);

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg screen capture.");
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddError(e.Data); };
        process.OutputDataReceived += (_, e) => ParseProgress(e.Data);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _process = process;
        _segmentClock.Restart();   // pointer timestamps are measured from the moment capture begins
    }

    void ParseProgress(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        int eq = line.IndexOf('=');
        if (eq <= 0) return;
        string key = line[..eq], value = line[(eq + 1)..].Trim();
        switch (key)
        {
            case "total_size":
                if (long.TryParse(value, NumberStyles.Integer, Inv, out long size)) Interlocked.Exchange(ref _liveBytes, size);
                break;
            case "drop_frames":
                if (int.TryParse(value, NumberStyles.Integer, Inv, out int drops)) _liveDropped = drops;
                break;
            case "speed":
                if (double.TryParse(value.TrimEnd('x', ' '), NumberStyles.Float, Inv, out double speed)) _speed = speed;
                break;
        }
    }

    void AddError(string message)
    {
        lock (_errorSync)
        {
            _errorLines.Enqueue(message.Trim());
            while (_errorLines.Count > 8) _errorLines.Dequeue();
        }
    }

    static int EvenUp(int value) => value % 2 == 0 ? value : value + 1;

    List<string> BuildArguments(string segmentPath)
    {
        string fps = _captureFps.ToString(Inv);
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "1", "-y"
        };

        // ---- input 0: the screen ----
        a.AddRange(new[] { "-thread_queue_size", "512", "-f", "gdigrab", "-framerate", fps });
        string cursor = _o.ShowCursor ? "1" : "0";
        if (_captureWindow != null)
            a.AddRange(new[] { "-draw_mouse", cursor, "-i", "title=" + _captureWindow.Title });
        else if (_captureArea != null)
        {
            a.AddRange(new[] { "-offset_x", _captureX.ToString(Inv), "-offset_y", _captureY.ToString(Inv), "-video_size", $"{_captureWidth}x{_captureHeight}" });
            a.AddRange(new[] { "-draw_mouse", cursor, "-i", "desktop" });
        }
        else a.AddRange(new[] { "-draw_mouse", cursor, "-i", "desktop" });

        int next = 1, cameraIndex = -1, micIndex = -1, systemIndex = -1;
        string camera = _o.WebcamDevice?.Trim() ?? "";
        if (camera.Length > 0)
        {
            a.AddRange(new[] { "-thread_queue_size", "512", "-rtbufsize", "128M", "-use_wallclock_as_timestamps", "1", "-f", "dshow", "-i", "video=" + camera });
            cameraIndex = next++;
        }
        if (_o.MicDevice != null)
        {
            a.AddRange(new[] { "-thread_queue_size", "512", "-use_wallclock_as_timestamps", "1", "-f", "dshow", "-audio_buffer_size", "40", "-i", "audio=" + _o.MicDevice });
            micIndex = next++;
        }
        string system = _o.SystemAudioDevice?.Trim() ?? "";
        if (system.Length > 0 && !RecorderAudioSource.IsWasapiLoopback(system))
        {
            a.AddRange(new[] { "-thread_queue_size", "512", "-use_wallclock_as_timestamps", "1", "-f", "dshow", "-audio_buffer_size", "40", "-i", "audio=" + system });
            systemIndex = next++;
        }

        // ---- video graph: even size -> optional downscale -> optional webcam bubble -> pixel format ----
        string pixelFormat = _encoderName is "h264_qsv" or "h264_mf" ? "nv12" : "yuv420p";
        string chain = "pad=ceil(iw/2)*2:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2";
        if (_targetHeight > 0) chain += $",scale=-2:'min({_targetHeight},ih)':flags=bicubic";
        int capH = EvenUp(_captureHeight), capW = EvenUp(_captureWidth);
        int outH = _targetHeight > 0 ? Math.Min(_targetHeight, capH) : capH;
        int outW = EvenUp((int)Math.Round(capW * (double)outH / Math.Max(2, capH)));

        var graph = new List<string>();
        if (cameraIndex < 0)
            graph.Add($"[0:v]{chain},format={pixelFormat}[v]");
        else if (_o.WebcamLayout == WebcamLayout.SideBySide)
        {
            int leftWidth = Math.Max(96, EvenUp(outW / 2));
            int rightWidth = Math.Max(96, outW - leftWidth);
            graph.Add($"[0:v]{chain},scale={leftWidth}:{outH}:force_original_aspect_ratio=decrease,pad={leftWidth}:{outH}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[screen]");
            graph.Add($"[{cameraIndex}:v]scale={rightWidth}:{outH}:force_original_aspect_ratio=decrease,pad={rightWidth}:{outH}:(ow-iw)/2:(oh-ih)/2:color=black{(_o.WebcamMirror ? ",hflip" : "")},setsar=1[cam]");
            graph.Add($"[screen][cam]hstack=inputs=2,format={pixelFormat}[v]");
        }
        else
        {
            int camPercent = _o.WebcamLayout == WebcamLayout.PresenterFocus ? 42 : Math.Clamp(_o.WebcamSizePercent, 8, 40);
            int camW = Math.Clamp(EvenUp((int)(outW * camPercent / 100.0)), 96, Math.Max(96, outW / 2));
            const int margin = 24;
            string x = _o.WebcamPosition is WebcamCorner.BottomLeft or WebcamCorner.TopLeft ? margin.ToString(Inv) : $"main_w-overlay_w-{margin}";
            string y = _o.WebcamPosition is WebcamCorner.TopLeft or WebcamCorner.TopRight ? margin.ToString(Inv) : $"main_h-overlay_h-{margin}";
            graph.Add($"[0:v]{chain}[bg]");
            graph.Add($"[{cameraIndex}:v]scale={camW}:-2{(_o.WebcamMirror ? ",hflip" : "")}[cam]");
            graph.Add($"[bg][cam]overlay={x}:{y},format={pixelFormat}[v]");
        }

        // ---- audio graph ----
        bool hasAudio = micIndex >= 0 || systemIndex >= 0;
        if (micIndex >= 0 && systemIndex >= 0)
            graph.Add($"[{micIndex}:a][{systemIndex}:a]amix=inputs=2:duration=longest:dropout_transition=0,aresample=async=1:first_pts=0[a]");
        else if (hasAudio)
            graph.Add($"[{(micIndex >= 0 ? micIndex : systemIndex)}:a]aresample=async=1:first_pts=0[a]");

        a.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[v]" });
        if (hasAudio) a.AddRange(new[] { "-map", "[a]" });

        // ---- video encoder ----
        bool lowRes = _targetHeight is > 0 and <= 540;
        int quality = _o.Quality switch { RecorderQuality.Small => 29, RecorderQuality.High => 18, _ => 23 };
        if (lowRes) quality += 4;
        if (_captureFps >= 60 && _o.Quality != RecorderQuality.High) quality += 2;
        string gop = (_captureFps * 2).ToString(Inv);
        a.AddRange(new[] { "-r", fps, "-fps_mode", "cfr", "-g", gop });
        switch (_encoderName)
        {
            case "h264_nvenc":
                a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "vbr", "-cq", quality.ToString(Inv), "-b:v", "0" });
                break;
            case "h264_qsv":
                a.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", quality.ToString(Inv) });
                break;
            case "h264_amf":
                a.AddRange(new[] { "-c:v", "h264_amf", "-usage", "lowlatency", "-quality", "speed", "-rc", "cqp",
                    "-qp_i", quality.ToString(Inv), "-qp_p", (quality + 2).ToString(Inv) });
                break;
            case "h264_mf":
                int bitrate = Math.Clamp(outW * outH * _captureFps / 30 / 180, 1_500_000, 24_000_000);
                a.AddRange(new[] { "-c:v", "h264_mf", "-b:v", bitrate.ToString(Inv) });
                break;
            default:
                string preset = _captureFps >= 60 || _o.Quality == RecorderQuality.Small ? "ultrafast"
                    : _o.Quality == RecorderQuality.High ? "veryfast" : "superfast";
                int threads = lowRes ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
                a.AddRange(new[] { "-c:v", "libx264", "-preset", preset, "-tune", "zerolatency", "-crf", quality.ToString(Inv),
                    "-threads", threads.ToString(Inv), "-sc_threshold", "0" });
                break;
        }

        if (hasAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2" });
        else a.Add("-an");

        // Fragmented MP4: every finished fragment is playable, so a crash or power cut never loses the whole take.
        a.AddRange(new[] { "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", segmentPath });
        return a;
    }

    // ---------------------------------------------------------------- segment lifecycle
    async Task FinalizeActiveSegmentAsync()
    {
        await StopLoopbackSegmentAsync();
        var process = _process;
        _process = null;
        if (process == null) return;
        string segmentPath = CurrentSegmentPath;
        if (!process.HasExited)
        {
            try { await process.StandardInput.WriteLineAsync("q"); await process.StandardInput.FlushAsync(); } catch { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } }
        }
        await process.WaitForExitAsync();
        int exit = process.ExitCode;
        process.Dispose();

        long length = File.Exists(segmentPath) ? new FileInfo(segmentPath).Length : 0;
        _droppedBase += _liveDropped; _liveDropped = 0;
        Interlocked.Exchange(ref _liveBytes, 0);
        if (length >= 1024)
        {
            Interlocked.Add(ref _finishedBytes, length);
            _segments.Add(segmentPath);   // fragmented MP4 stays usable even if FFmpeg exited with an error
        }
        if (exit != 0 && length < 1024)
            throw new InvalidOperationException("Screen capture did not finish cleanly. " + (LastError ?? "Check FFmpeg setup, device names and screen-capture permissions."));
        if (length < 1024)
            throw new InvalidOperationException("The recording segment contains no usable video frames.");
    }

    async Task AbortActiveSegmentAsync()
    {
        await StopLoopbackSegmentAsync();
        var process = _process;
        _process = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("q"); await process.StandardInput.FlushAsync(); } catch { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { process.Kill(true); } catch { } }
            }
        }
        catch { }
        finally { process.Dispose(); }
    }

    async Task JoinSegmentsAsync()
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (_segments.Count == 1) args.AddRange(new[] { "-i", _segments[0] });
        else
        {
            string listFile = Path.Combine(_segmentDirectory!, "concat.txt");
            File.WriteAllLines(listFile, _segments.Select(p => "file '" + p.Replace("\\", "/").Replace("'", "'\\''") + "'"));
            args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", listFile });
        }
        // Remux (no re-encode) into a regular MP4 with the index at the front: instant, lossless, editor-friendly.
        args.AddRange(new[] { "-c", "copy", "-movflags", "+faststart", _outputPath! });

        var start = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not finish the recording file.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not finish the recording file. " + error);
        if (RecorderAudioSource.IsWasapiLoopback(_o.SystemAudioDevice))
            await MixLoopbackAudioAsync();
    }

    async Task MixLoopbackAudioAsync()
    {
        if (_loopbackSegments.Count == 0 || _outputPath == null || _segmentDirectory == null)
            throw new InvalidOperationException("WASAPI did not produce a usable desktop-audio recording.");

        string systemAudio = Path.Combine(_segmentDirectory, "system-audio.wav");
        string listPath = Path.Combine(_segmentDirectory, "system-audio-parts.txt");
        File.WriteAllLines(listPath, _loopbackSegments.Select(p => "file '" + p.Replace("\\", "/").Replace("'", "'\\''") + "'"));
        var audioJoin = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", systemAudio })
            audioJoin.ArgumentList.Add(arg);
        using (var join = Process.Start(audioJoin) ?? throw new InvalidOperationException("Could not join WASAPI audio parts."))
        {
            string joinError = await join.StandardError.ReadToEndAsync();
            await join.WaitForExitAsync();
            if (join.ExitCode != 0) throw new InvalidOperationException("Could not join WASAPI audio parts. " + joinError);
        }
        string mixedPath = Path.Combine(_segmentDirectory, "recording-with-system-audio.mp4");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", _outputPath, "-i", systemAudio };
        if (!string.IsNullOrWhiteSpace(_o.MicDevice))
            args.AddRange(new[] { "-filter_complex", "[0:a:0][1:a:0]amix=inputs=2:duration=longest:dropout_transition=0,aresample=async=1:first_pts=0[a]", "-map", "0:v:0", "-map", "[a]" });
        else args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
        args.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", mixedPath });

        var start = new ProcessStartInfo(FFmpegService.ResolveTool("ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not combine desktop audio with the recording.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(mixedPath) || new FileInfo(mixedPath).Length < 1024)
            throw new InvalidOperationException("Could not combine WASAPI desktop audio with the recording. " + error);
        File.Move(mixedPath, _outputPath, true);
    }

    // ---------------------------------------------------------------- pointer tracking
    void InstallHook()
    {
        if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new InvalidOperationException("Windows did not allow pointer tracking. Try running MyEditor normally and check accessibility/security settings.");
        }
        if (_o.TrackTyping && _keyboardHook == IntPtr.Zero)
            _keyboardHook = SetWindowsHookExKeyboard(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
    }

    IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsRecording)
        {
            if (_captureWindow != null && TryGetClientBounds(_captureWindow.Handle, out int wx, out int wy, out int ww, out int wh))
            { _captureX = wx; _captureY = wy; _captureWidth = ww; _captureHeight = wh; }
            int message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            bool click = message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;
            string button = message switch
            {
                WmRButtonDown => "Right",
                WmMButtonDown => "Middle",
                WmXButtonDown when (data.MouseData >> 16) == 2 => "Back",
                WmXButtonDown => "Forward",
                _ => "Left"
            };
            if (click || message == WmMouseMove)
            {
                double seconds = ElapsedSeconds;
                int dx = Math.Abs(data.Point.X - _lastX), dy = Math.Abs(data.Point.Y - _lastY);
                bool significantMove = message == WmMouseMove && dx + dy >= 5 && seconds - _lastMoveAt >= 0.04 && _movesRecorded < 30000;
                if ((click || significantMove) && _events.Count < MaxEvents &&
                    data.Point.X >= _captureX && data.Point.Y >= _captureY && data.Point.X < _captureX + _captureWidth && data.Point.Y < _captureY + _captureHeight)
                {
                    _events.Add(new PointerEvent(seconds,
                        Math.Clamp((double)(data.Point.X - _captureX) / _captureWidth, 0, 1),
                        Math.Clamp((double)(data.Point.Y - _captureY) / _captureHeight, 0, 1), click) { Button = button });
                    if (!click) _movesRecorded++;
                    _lastMoveAt = seconds; _lastX = data.Point.X; _lastY = data.Point.Y;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsRecording && wParam.ToInt32() is WmKeyDown or WmSysKeyDown && _events.Count < MaxEvents)
        {
            var key = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            int vk = (int)key.VirtualKey;
            bool typingKey = vk is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x69 or 0x08 or 0x09 or 0x0D or 0x20 or >= 0xBA and <= 0xE2;
            bool modified = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            double seconds = ElapsedSeconds;
            if (typingKey && !modified && seconds - _lastTypingAt >= 0.55 && GetCursorPos(out var point) &&
                point.X >= _captureX && point.Y >= _captureY && point.X < _captureX + _captureWidth && point.Y < _captureY + _captureHeight)
            {
                _events.Add(new PointerEvent(seconds,
                    Math.Clamp((double)(point.X - _captureX) / _captureWidth, 0, 1),
                    Math.Clamp((double)(point.Y - _captureY) / _captureHeight, 0, 1), false)
                    { IsTyping = true, Button = "Typing", ZoomStrength = 1.14, ZoomDurationSeconds = 1.07, ZoomSpeedSeconds = 0.24, ZoomEasing = "Smooth" });
                _lastTypingAt = seconds;
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    void Unhook()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
    }

    public void Dispose()
    {
        Unhook();
        if (_process is { HasExited: false }) { try { _process.Kill(true); } catch { } }
        _process?.Dispose(); _process = null;
    }

    // ---------------------------------------------------------------- native
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MouseHookData
    {
        public NativePoint Point; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardHookData { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    delegate IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam);
    delegate IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, MouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] static extern IntPtr SetWindowsHookExKeyboard(int idHook, KeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr GetModuleHandle(string? moduleName);

    static bool TryGetClientBounds(IntPtr handle, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!IsWindow(handle) || !GetClientRect(handle, out var rect)) return false;
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin)) return false;
        x = origin.X; y = origin.Y; width = rect.Right - rect.Left; height = rect.Bottom - rect.Top;
        return width >= 2 && height >= 2;
    }
}
