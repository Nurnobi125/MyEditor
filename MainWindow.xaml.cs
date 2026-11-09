using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Interop;
using Microsoft.Win32;
using VideoEditor.Models;
using VideoEditor.Services;

namespace VideoEditor;

sealed record Snapshot(List<Clip> Main, List<OverlayClip> Ov, List<AudioClip> Au, double Playhead, int TextLaneCount, int AudioLaneCount,
    Dictionary<string, bool> MutedTracks, Dictionary<string, bool> LockedTracks);
sealed record WorkspaceLayout(double Width, double Height, double Left, double Top, bool Maximized,
    double MediaRatio, double InspectorRatio, double UpperRatio);
sealed record AnimationOption(string Name, string Family, int Variation);
sealed record TextTemplateOption(string Name, string Color, string Shape, int FontSize, double Y, string Animation, string Category = "Titles");
sealed record ZoomPresetOption(string Name, double Strength, double Speed, double Duration, string Easing);
sealed record TrackControlAction(string Track, string Action);
sealed record FloatingPanelState(FrameworkElement Panel, Border Placeholder, int ChildIndex, Window Window, string Title);
sealed record DockGroupState(Window Window, FrameworkElement[] Panels, Border[] Placeholders, int[] Columns);

public partial class MainWindow : Window
{
    const int WmHotkey = 0x0312;
    const int HotkeyStartResume = 0x7301, HotkeyPauseResume = 0x7302, HotkeyStop = 0x7303, HotkeyShowControls = 0x7304;
    const uint ModAlt = 0x0001, ModControl = 0x0002;
    static readonly Regex SrtCuePattern = new(
        @"(?<start>\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2},\d{3})\s*\r?\n(?<text>.*?)(?=\r?\n\r?\n|\z)",
        RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly ExportSettings[] Presets =
    {
        new("540p 24fps · Low-end PC", 960, 540, 24, false, true),
        new("720p 30fps · Fast", 1280, 720, 30),
        new("1080p 30fps", 1920, 1080, 30),
        new("1080p 60fps", 1920, 1080, 60),
        new("4K 30fps",    3840, 2160, 30),
        new("4K 60fps",    3840, 2160, 60),
    };
    static readonly string[] AnimationFamilies = { "Zoom In", "Zoom Out", "Spin In", "Spin Out", "Bubble Pop", "Bounce", "Fade In", "Slide Left", "Slide Right", "Slide Up", "Slide Down", "Pulse", "Shake", "Flip", "Elastic", "Wipe", "Drift", "Rise", "Drop", "Grow", "Shrink", "Twist", "Float", "Pop" };
    static readonly AnimationOption[] AnimationPresets = AnimationFamilies
        .SelectMany(f => Enumerable.Range(1, 10).Select(v => new AnimationOption($"{f} · {v:00}", f, v)))
        .ToArray();
    // Name, text colour, box shape, animation family, Y (-1 = original spacing), extra font size, gallery category.
    static readonly TextTemplateOption[] TextTemplates = new (string Name, string Color, string Shape, string Anim, double Y, int Boost, string Cat)[]
    {
        ("Clean title", "White", "None", "Fade In", -1, 0, "Titles"), ("Bold headline", "Yellow", "Highlight box", "Pop", -1, 0, "Titles"),
        ("Cinematic lower third", "White", "Caption box", "Slide Left", -1, 0, "Lower thirds"), ("Creator caption", "Cyan", "Caption box", "Rise", -1, 0, "Captions"),
        ("Breaking news", "Red", "Highlight box", "Wipe", -1, 0, "Promo"), ("Soft subtitle", "White", "Caption box", "Drift", -1, 0, "Captions"),
        ("Neon callout", "Cyan", "Highlight box", "Zoom In", -1, 0, "Promo"), ("Minimal label", "Yellow", "None", "Slide Up", -1, 0, "Lower thirds"),
        ("Retro title", "Yellow", "Caption box", "Spin In", -1, 0, "Titles"), ("Elegant intro", "White", "None", "Float", -1, 0, "Titles"),
        ("Vlog intro", "White", "None", "Bubble Pop", .50, 6, "Social"), ("Shorts hook", "Yellow", "None", "Elastic", .30, 10, "Social"),
        ("Gaming hit", "Yellow", "Highlight box", "Shake", .50, 4, "Social"), ("Label tag", "Black", "Highlight box", "Pop", .16, -6, "Social"),
        ("Tutorial step", "Cyan", "Caption box", "Slide Right", .12, -4, "Captions"), ("Calm caption", "White", "Caption box", "Float", .88, -6, "Captions"),
        ("Pro lower third", "White", "Caption box", "Slide Up", .84, -4, "Lower thirds"), ("Name tag", "Cyan", "Highlight box", "Slide Left", .80, -8, "Lower thirds"),
        ("Quote card", "White", "Caption box", "Fade In", .50, 2, "Titles"), ("Chapter marker", "Yellow", "Caption box", "Wipe", .12, -2, "Titles"),
        ("Outro thanks", "White", "None", "Grow", .50, 8, "Titles"), ("Credits roll", "White", "None", "Rise", .50, -4, "Titles"),
        ("Sale banner", "Red", "Highlight box", "Bounce", .18, 4, "Promo"), ("Flash warning", "Red", "Caption box", "Pulse", .15, 0, "Promo"),
        ("Night neon", "Cyan", "None", "Pulse", .50, 6, "Promo"),
        // ---- Premium pack: gold / pink / violet colours with polished boxes and smooth motion ----
        ("Gold luxe title", "Gold", "None", "Fade In", .50, 8, "Premium"), ("Royal intro", "Violet", "Caption box", "Zoom In", .50, 6, "Premium"),
        ("Diamond lower third", "White", "Caption box", "Slide Left", .84, -4, "Premium"), ("Glam caption", "Pink", "Caption box", "Rise", .86, -4, "Premium"),
        ("VIP badge", "Gold", "Highlight box", "Pop", .14, -6, "Premium"), ("Rose gold quote", "Pink", "None", "Float", .50, 2, "Premium"),
        ("Platinum headline", "White", "Highlight box", "Wipe", .30, 8, "Premium"), ("Luxury sale", "Gold", "Caption box", "Bounce", .18, 6, "Premium"),
        ("Cinema credits", "Gold", "None", "Drift", .50, -2, "Premium"), ("Neon pink hook", "Pink", "None", "Elastic", .30, 10, "Premium"),
        ("Crown outro", "Gold", "None", "Grow", .50, 10, "Premium"), ("Elite name tag", "Violet", "Highlight box", "Slide Up", .80, -8, "Premium")
    }.SelectMany((t, i) => Enumerable.Range(1, 5).Select(v => new TextTemplateOption(
        $"{t.Name} · {v:00}", t.Color, t.Shape, 34 + v * 8 + t.Boost,
        t.Y < 0 ? (i % 3 == 0 ? .5 : .70 + v * .035) : Math.Clamp(t.Y + (v - 3) * .012, .08, .92),
        $"{t.Anim} · {v:00}", t.Cat))).ToArray();
    static readonly ZoomPresetOption[] ZoomPresets = new (string, double, double, double, string)[]
    {
        ("Gentle focus", 1.18, .30, 1.25, "Cinematic"), ("Presenter focus", 1.28, .18, 1.0, "Smooth"),
        ("Detail zoom", 1.55, .12, .85, "Snappy"), ("Punch in", 1.75, .08, .65, "Snappy"),
        ("Cinematic hold", 1.38, .42, 2.1, "Cinematic"), ("Soft tutorial", 1.22, .24, 1.6, "Smooth")
    }.SelectMany((z, i) => Enumerable.Range(1, 5).Select(v => new ZoomPresetOption(
        $"{z.Item1} · {v:00}", 1 + (z.Item2 - 1) * v / 5.0, z.Item3 + (1 - z.Item3) * (5 - v) / 12.0,
        z.Item4 * (0.8 + v * .08), z.Item5))).ToArray();
    static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    static readonly string[] AudExt = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg" };

    // timeline lanes (y positions inside the canvas)
    const double LaneMainY = 32, LaneMainH = 56;
    const double LaneOvY = 94, LaneOvH = 34;
    const double LaneOv3Y = 132, LaneOv4Y = 170;
    const double LaneTextY = 208, LaneTextH = 30, TextLanePitch = 36;
    const double LaneAudioH = 26, LaneAudioGap = 4;
    const double TrackLabelWidth = 118;
    const int MaxTimelineLanes = 12;

    readonly ObservableCollection<MediaItem> _bin = new();
    List<Clip> _timeline = new();
    List<OverlayClip> _overlays = new();
    List<AudioClip> _audios = new();
    readonly Stack<Snapshot> _undo = new();
    readonly Stack<Snapshot> _redo = new();
    readonly Dictionary<string, bool> _mutedTracks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> _lockedTracks = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<object> _multiSelection = new();
    readonly List<(object Item, double Start, double Left, double Top)> _dragGroup = new();
    readonly Dictionary<AudioClip, MediaElement> _audioPlayers = new();
    readonly Dictionary<OverlayClip, MediaElement> _overlayVideoPlayers = new();
    readonly Dictionary<string, BitmapSource> _overlayImageCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, BitmapSource> _waveformCache = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _thumbnailSemaphore = new(1, 1);
    readonly SemaphoreSlim _waveformSemaphore = new(1, 1);

    Clip? _selected;
    Clip? _previewClip;
    Clip? _zoomActionCacheClip;
    List<(PointerEvent Action, double Time)>? _zoomActionCache;
    OverlayClip? _selOv;
    AudioClip? _selAu;
    PointerEvent? _selectedZoomEvent, _dragZoomEvent;
    Clip? _zoomEventClip;
    readonly Dictionary<PointerEvent, Clip> _zoomMarkerOwners = new();
    Rectangle? _selectionMarquee;
    bool _marqueeSelecting;
    Grid? _previewParent;
    int _previewParentIndex;
    Window? _fullscreenPreviewWindow;

    double _playhead;
    bool _playing;
    bool _loading;
    bool _updatingPreviewSeek;
    string? _loadedPath;
    string? _currentProjectPath;
    bool _projectDirty;
    double _pendingSeek = -1;
    CancellationTokenSource? _livePreviewCts;
    int _livePreviewGeneration;
    Line? _playLine;
    DateTime _last;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    ScreenRecorderService? _screenRecorder;
    RecorderOptions _recorderOptions = RecorderOptions.Load();
    bool _recordStopping;
    DesignStudioWindow? _designStudio;
    RecordingControlWindow? _recordControlWindow;
    HwndSource? _hotkeySource;
    string? _recordingPath;
    CaptureArea? _captureArea;
    CaptureWindowInfo? _captureWindow;
    bool _allowClose, _closingAfterRecord, _restoreEditorAfterRecording;
    bool _useLowMemoryProxies;
    int _textLaneCount = 3, _audioLaneCount = 3;
    WorkspaceLayout? _savedWorkspaceLayout;
    string _workspacePreset = "Classic";
    bool _applyingWorkspacePreset;
    SourceMonitorWindow? _sourceMonitor;
    readonly Dictionary<string, FloatingPanelState> _floatingPanels = new(StringComparer.OrdinalIgnoreCase);
    readonly List<DockGroupState> _floatingGroups = new();
    string[] _panelOrder = { "Media", "Program", "Inspector" };
    static string WorkspacePresetPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "workspace-preset.txt");
    static string PanelOrderPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "workspace-panel-order.txt");
    const string GapPreviewToken = "::myeditor-gap::";
    bool _applyingPerformanceProfile;
    bool _textTypingUndoRecorded;
    bool _textKeyEdit;
    bool _audioKeyEdit;
    HashSet<string> _availableEncoders = new(StringComparer.OrdinalIgnoreCase) { "h264" };
    string _backgroundMode = "Black bars", _backgroundColor = "#151820", _backgroundImagePath = "";
    static readonly JsonSerializerOptions ProjectJson = new() { WriteIndented = true };
    static string AutosavePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "autosave.myeditor");
    static string AutosaveBackupPath => AutosavePath + ".bak";
    static string AutosaveTempPath => AutosavePath + ".tmp";
    static string WorkspaceLayoutPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor", "workspace-layout.json");

    int _drag;            // 0 none, 1 overlay/text move, 2 audio move, 3 V1 reorder, 4 zoom, 5 left trim, 6 right trim
    bool _dragStarted;
    double _dragX, _dragY, _dragOrig, _dragGrabOffset, _dragTargetTime;
    double _dragOrigDuration, _dragOrigIn, _dragOrigRight, _dragOrigVisualWidth;
    Point _binDragStart;
    bool _binDragPending;
    int _dragTargetTrack = 2;
    int _dragTargetTextTrack = 1;
    int _activeAudioTrack = 1;
    Clip? _dragMainClip;
    OverlayClip? _dragOverlay;
    AudioClip? _dragAudio;
    Border? _dragVisual;
    Line? _dropIndicator;
    readonly Dictionary<object, Border> _timelineBlocks = new();
    OverlayClip[]? _lastGuideActiveOverlays;
    readonly HashSet<AudioClip> _audioPlaybackActive = new();

    double Pps => ZoomSlider.Value;
    static double VideoLaneY(int track) => track switch { 3 => LaneOv3Y, 4 => LaneOv4Y, _ => LaneOvY };
    static int VideoTrackAt(double y) => y < LaneOv3Y ? 2 : y < LaneOv4Y ? 3 : 4;
    double LaneAudioY => LaneTextY + _textLaneCount * TextLanePitch;
    double AudioLaneTop(int track) => LaneAudioY + (Math.Clamp(track, 1, _audioLaneCount) - 1) * (LaneAudioH + LaneAudioGap);
    int AudioTrackAt(double y) => Math.Clamp((int)((y - LaneAudioY) / (LaneAudioH + LaneAudioGap)) + 1, 1, _audioLaneCount);
    static double TextLaneY(int track) => LaneTextY + (Math.Max(1, track) - 1) * TextLanePitch;
    int TextTrackAt(double y) => Math.Clamp((int)((y - LaneTextY) / TextLanePitch) + 1, 1, _textLaneCount);
    Canvas VideoCanvasForTrack(int track) => track switch { 3 => OverlayVideoCanvas3, 4 => OverlayVideoCanvas4, _ => OverlayVideoCanvas2 };
    Canvas ImageCanvasForTrack(int track) => track switch { 3 => OverlayImageCanvas3, 4 => OverlayImageCanvas4, _ => OverlayImageCanvas2 };
    double Total => _timeline.Sum(c => c.Duration);
    double Extent => Math.Max(Total, Math.Max(
        _overlays.Count > 0 ? _overlays.Max(o => o.End) : 0,
        _audios.Count > 0 ? _audios.Max(a => a.Start + a.Duration) : 0));

    public MainWindow()
    {
        InitializeComponent();
        _loading = true; SyncQuickBackgroundBox(); _loading = false;
        BuildMotionPanel();
        RecordTypingCheck.IsChecked = _recorderOptions.AutoZoomOnTyping;
        TrackTypingMenuItem.IsChecked = _recorderOptions.AutoZoomOnTyping;
        LoadWorkspaceLayout();
        try { if (File.Exists(WorkspacePresetPath)) _workspacePreset = File.ReadAllText(WorkspacePresetPath).Trim(); } catch { }
        try
        {
            if (File.Exists(PanelOrderPath))
            {
                var restored = File.ReadAllText(PanelOrderPath).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (restored.Length == 3 && restored.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { "Media", "Program", "Inspector" })) _panelOrder = restored;
            }
        }
        catch { }
        FilterBox.ItemsSource = new[] { "None", "Black & White", "Vintage", "Warm", "Cool", "Vivid" }
            .Concat(EffectCatalog.All.Select(p => p.Name)).ToArray();
        FilterBox.SelectedIndex = 0;
        AnimationBox.ItemsSource = new[] { "None" }.Concat(AnimationPresets.Select(p => p.Name)).ToArray();
        AnimationBox.SelectedIndex = 0;
        TextTemplateBox.ItemsSource = TextTemplates.Select(t => t.Name).ToArray();
        ZoomPresetBox.ItemsSource = ZoomPresets.Select(z => z.Name).ToArray();
        TextTemplateShelfBox.ItemsSource = TextTemplates.Select(t => t.Name).ToArray();
        TemplateShelfBox.ItemsSource = TextTemplates.Select(t => t.Name).ToArray();
        EffectShelfBox.ItemsSource = EffectCatalog.All.Select(p => p.Name).ToArray();
        TransitionShelfBox.ItemsSource = TransitionBox.Items.Cast<ComboBoxItem>().Select(i => i.Content?.ToString() ?? "None").ToArray();
        FilterShelfBox.ItemsSource = new[] { "None", "Black & White", "Vintage", "Warm", "Cool", "Vivid" };
        TextTemplateShelfBox.SelectedIndex = TemplateShelfBox.SelectedIndex = EffectShelfBox.SelectedIndex = 0;
        InitTemplateGallery();
        TransitionShelfBox.SelectedIndex = FilterShelfBox.SelectedIndex = 0;
        RenderOptions.SetBitmapScalingMode(PreviewImage, BitmapScalingMode.LowQuality);
        RenderOptions.SetEdgeMode(TimelineCanvas, EdgeMode.Aliased);
        BinList.ItemsSource = CollectionViewSource.GetDefaultView(_bin);
        CollectionViewSource.GetDefaultView(_bin).Filter = item =>
            item is MediaItem media && (string.IsNullOrWhiteSpace(MediaSearch?.Text) ||
            media.Name.Contains(MediaSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        _timer.Tick += Tick;
        _autosaveTimer.Tick += (_, _) => AutoSaveProject();
        _autosaveTimer.Start();
        _recordTimer.Tick += (_, _) =>
        {
            bool paused = _screenRecorder?.IsPaused == true;
            Status.Text = $"{(paused ? "Recording paused" : "Recording screen")} · {TimeSpan.FromSeconds(_screenRecorder?.ElapsedSeconds ?? 0):hh\\:mm\\:ss} · {_screenRecorder?.RecordingDescription}";
            _recordControlWindow?.UpdateRecordingState(paused, _screenRecorder?.ElapsedSeconds ?? 0);
            if (_screenRecorder is { } rec)
            {
                bool struggling = rec.IsRecording && ((rec.EncodeSpeed > 0 && rec.EncodeSpeed < 0.93) || rec.DroppedFrames > 0);
                _recordControlWindow?.UpdateStats($"{rec.FileSizeBytes / 1048576.0:0.0} MB · {rec.DroppedFrames} dropped · {rec.EncoderLabel}", struggling);
                if (!_recordStopping && rec.LimitReached)
                    _ = AutoStopRecordingAsync("Time limit reached");
                else if (!_recordStopping && rec.CaptureEnded)
                    _ = RecoverCaptureOrStopAsync(rec);
            }
        };
        _recordTimer.Tick += (_, _) => _recordControlWindow?.UpdateRecordingState(_screenRecorder?.IsPaused == true, _screenRecorder?.ElapsedSeconds ?? 0);
        SourceInitialized += (_, _) => RegisterRecorderHotkeys();
        TimelineScroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            double oldPps = Pps;
            double mouseX = e.GetPosition(TimelineScroll).X;
            double timeAtMouse = (TimelineScroll.HorizontalOffset + mouseX - TrackLabelWidth) / oldPps;
            ZoomSlider.Value = Math.Clamp(oldPps * (e.Delta > 0 ? 1.15 : 1 / 1.15), ZoomSlider.Minimum, ZoomSlider.Maximum);
            TimelineScroll.UpdateLayout();
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, TrackLabelWidth + timeAtMouse * Pps - mouseX));
            e.Handled = true;
        };
        Closing += async (_, e) =>
        {
            foreach (var group in _floatingGroups.ToArray()) RestoreDockGroup(group, closeWindow: true);
            foreach (var id in _floatingPanels.Keys.ToArray()) RestoreWorkspacePanel(id, closeWindow: true);
            SaveWorkspaceLayout();
            AutoSaveProject();
            if (_allowClose || _screenRecorder == null) { UnregisterRecorderHotkeys(); return; }
            e.Cancel = true;
            if (_closingAfterRecord) return;
            _closingAfterRecord = true;
            try { await StopScreenRecordingAsync(openInEditor: false); }
            catch { }
            _allowClose = true;
            Close();
        };
        Loaded += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(RestoreWorkspacePanels, System.Windows.Threading.DispatcherPriority.Loaded);
            ApplyPanelOrder();
            ApplyWorkspacePreset(_workspacePreset, persist: false, applyLayout: false);
            Refresh();
            await OfferAutosaveRecoveryAsync();
            await SetupMediaToolsAsync();
            await LoadRecordingAudioDevicesAsync();
            await RefreshEncoderAvailabilityAsync();
            ApplyPerformanceProfile();
        };

        // one undo checkpoint per slider drag
        foreach (var sl in new[] { TrimInSlider, TrimOutSlider, SpeedSlider, BrightnessSlider, ContrastSlider, SaturationSlider, TemperatureSlider, TintSlider,
                                   ExposureSlider, HighlightsSlider, ShadowsSlider, HueSlider, VibranceSlider, SharpnessSlider, VignetteSlider,
                                   ClickEffectSizeSlider, ClickEffectDurationSlider, VolumeSlider,
                                   LDurSlider, LXSlider, LYSlider, LScaleSlider, LOpacitySlider, LFontSlider,
                                   AVolumeSlider, AFadeInSlider, AFadeOutSlider, ADurSlider, GapDurationSlider, MouseZoomStrengthSlider, MouseZoomSpeedSlider, MouseZoomDurationSlider })
            sl.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { if (HasSelection) Checkpoint(); }));
    }

    void LoadWorkspaceLayout()
    {
        try
        {
            if (!File.Exists(WorkspaceLayoutPath)) return;
            _savedWorkspaceLayout = JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(WorkspaceLayoutPath));
            if (_savedWorkspaceLayout is not { } saved) return;
            var area = SystemParameters.WorkArea;
            Width = Math.Clamp(saved.Width, Math.Min(MinWidth, area.Width), area.Width);
            Height = Math.Clamp(saved.Height, Math.Min(MinHeight, area.Height), area.Height);
            Left = Math.Clamp(saved.Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(saved.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch { _savedWorkspaceLayout = null; }
    }

    void RestoreWorkspacePanels()
    {
        if (_savedWorkspaceLayout is not { } saved || WorkspaceGrid.ActualWidth < 500) return;
        double width = Math.Max(0, WorkspaceGrid.ActualWidth - 12);
        double minMedia = MediaPaneColumn.MinWidth, minInspector = InspectorPaneColumn.MinWidth;
        double maxMedia = Math.Max(minMedia, width - 220 - minInspector);
        double media = Math.Clamp(saved.MediaRatio * width, minMedia, maxMedia);
        double inspector = Math.Clamp(saved.InspectorRatio * width, minInspector, Math.Max(minInspector, width - media - 220));
        MediaPaneColumn.Width = new GridLength(media);
        InspectorPaneColumn.Width = new GridLength(inspector);
        double upperRatio = Math.Clamp(saved.UpperRatio, 0.38, 0.78);
        UpperWorkspaceRow.Height = new GridLength(upperRatio, GridUnitType.Star);
        TimelineWorkspaceRow.Height = new GridLength(1 - upperRatio, GridUnitType.Star);
    }

    void SaveWorkspaceLayout()
    {
        try
        {
            if (WindowState == WindowState.Minimized) return;
            double width = WorkspaceGrid.ActualWidth;
            double panes = Math.Max(0, width - 12);
            double vertical = UpperWorkspaceRow.ActualHeight + TimelineWorkspaceRow.ActualHeight;
            Rect bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
            var layout = new WorkspaceLayout(bounds.Width, bounds.Height, bounds.Left, bounds.Top,
                WindowState == WindowState.Maximized,
                panes > 0 ? MediaPaneColumn.ActualWidth / panes : 0.33,
                panes > 0 ? InspectorPaneColumn.ActualWidth / panes : 0.22,
                vertical > 0 ? UpperWorkspaceRow.ActualHeight / vertical : 0.62);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WorkspaceLayoutPath)!);
            File.WriteAllText(WorkspaceLayoutPath, JsonSerializer.Serialize(layout));
        }
        catch { /* Workspace preferences must never prevent the editor from closing. */ }
    }

    void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TopActionsScroll == null) return;
        TopActionsScroll.Width = Math.Max(170, ActualWidth - 200);
        bool compact = ActualWidth < 1080;
        EffectsTopButton.Content = compact ? "✧" : "✨ Effects";
        StickersTopButton.Content = compact ? "✦" : "✦ Stickers";
        ShapesTopButton.Content = compact ? "◆" : "◆ Shapes";
        VideoLabTopButton.Content = compact ? "🎨" : "🎨 Video Lab";
        RecordBtn.Content = _screenRecorder != null ? "■  Stop" : compact ? "● Rec" : "●  Record";
        if (ExportBtn != null) ExportBtn.Content = compact ? "Export ↗" : "Export  ⤴";
    }

    void WorkspaceSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => SaveWorkspaceLayout();

    void WorkspacePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name }) ApplyWorkspacePreset(name, persist: true);
    }

    void ResetWorkspace_Click(object sender, RoutedEventArgs e) => ApplyWorkspacePreset(_workspacePreset, persist: true);

    void PanelFloat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id }) return;
        FrameworkElement panel = id switch { "Media" => MediaPanelHost, "Program" => ProgramPanelHost, "Inspector" => InspectorPanelHost, _ => null! };
        if (panel != null) FloatWorkspacePanel(id, panel, id switch { "Media" => "Media", "Program" => "Program Monitor", _ => "Inspector" });
    }

    void FloatWorkspacePanel(string id, FrameworkElement panel, string title)
    {
        if (_floatingPanels.ContainsKey(id)) { _floatingPanels[id].Window.Activate(); return; }
        if (panel.Parent is not Panel parent) return;
        int index = parent.Children.IndexOf(panel);
        var placeholder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 29, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 65, 84)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = $"{title} is floating\nChoose View → Panels → Dock all panels to restore it.",
                Foreground = new SolidColorBrush(Color.FromRgb(166, 170, 186)), TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18) }
        };
        int row = Grid.GetRow(panel), column = Grid.GetColumn(panel), rowSpan = Grid.GetRowSpan(panel), columnSpan = Grid.GetColumnSpan(panel);
        parent.Children.Remove(panel);
        parent.Children.Insert(Math.Clamp(index, 0, parent.Children.Count), placeholder);
        Grid.SetRow(placeholder, row); Grid.SetColumn(placeholder, column); Grid.SetRowSpan(placeholder, rowSpan); Grid.SetColumnSpan(placeholder, columnSpan);
        var window = new Window
        {
            Title = $"MyEditor · {title}", Owner = this, Width = Math.Max(480, panel.ActualWidth), Height = Math.Max(320, panel.ActualHeight),
            MinWidth = 360, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(20, 21, 29)), Content = panel
        };
        var state = new FloatingPanelState(panel, placeholder, index, window, title);
        _floatingPanels[id] = state;
        window.Closed += (_, _) => RestoreWorkspacePanel(id, closeWindow: false);
        window.Show();
    }

    void DockAllPanels_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _floatingGroups.ToArray()) RestoreDockGroup(group, closeWindow: true);
        foreach (var id in _floatingPanels.Keys.ToArray()) RestoreWorkspacePanel(id, closeWindow: true);
    }

    FrameworkElement? WorkspacePanel(string id) => id switch
    {
        "Media" => MediaPanelHost, "Program" => ProgramPanelHost, "Inspector" => InspectorPanelHost, _ => null
    };

    void ApplyPanelOrder()
    {
        int[] columns = { 0, 2, 4 };
        for (int i = 0; i < _panelOrder.Length && i < columns.Length; i++)
            if (WorkspacePanel(_panelOrder[i]) is { } panel) Grid.SetColumn(panel, columns[i]);
    }

    void WorkspacePanel_DragStart(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: string id }) return;
        var data = new DataObject("MyEditorWorkspacePanel", id);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    void WorkspacePanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("MyEditorWorkspacePanel") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void WorkspacePanel_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement target || !e.Data.GetDataPresent("MyEditorWorkspacePanel") ||
            e.Data.GetData("MyEditorWorkspacePanel") is not string sourceId) return;
        string targetId = target.Tag as string ?? (target == MediaPanelHost ? "Media" : target == ProgramPanelHost ? "Program" : target == InspectorPanelHost ? "Inspector" : "");
        if (targetId.Length == 0 || targetId == sourceId || _floatingPanels.ContainsKey(sourceId) || _floatingPanels.ContainsKey(targetId)) return;
        var targetPanel = WorkspacePanel(targetId);
        if (targetPanel == null) return;
        Point p = e.GetPosition(targetPanel);
        if (p.X > targetPanel.ActualWidth * .28 && p.X < targetPanel.ActualWidth * .72 &&
            p.Y > targetPanel.ActualHeight * .20 && p.Y < targetPanel.ActualHeight * .80)
        {
            GroupPanelsInTabs(sourceId, targetId);
            e.Handled = true;
            return;
        }

        // This workspace is a three-column layout, so left/right edge drops reorder panels in-place.
        // Top/bottom drops create a tab group because the main workspace has no horizontal panel rows.
        var order = _panelOrder.ToList();
        order.Remove(sourceId);
        int targetIndex = order.IndexOf(targetId);
        bool before = p.X <= targetPanel.ActualWidth * .28;
        if (p.Y < targetPanel.ActualHeight * .20 || p.Y > targetPanel.ActualHeight * .80) before = p.X < targetPanel.ActualWidth / 2;
        order.Insert(Math.Clamp(targetIndex + (before ? 0 : 1), 0, order.Count), sourceId);
        var columns = new[] { 0, 2, 4 };
        for (int i = 0; i < order.Count; i++)
            if (WorkspacePanel(order[i]) is { } panel) Grid.SetColumn(panel, columns[i]);
        _panelOrder = order.ToArray();
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PanelOrderPath)!); File.WriteAllText(PanelOrderPath, string.Join(',', _panelOrder)); } catch { }
        SaveWorkspaceLayout();
        Status.Text = $"{sourceId} panel docked { (before ? "before" : "after") } {targetId}";
        e.Handled = true;
    }

    void GroupPanelsInTabs(string sourceId, string targetId)
    {
        var source = WorkspacePanel(sourceId); var target = WorkspacePanel(targetId);
        if (source?.Parent is not Panel || target?.Parent is not Panel || source.Parent != WorkspaceGrid || target.Parent != WorkspaceGrid) return;
        var panels = new[] { source, target };
        var columns = panels.Select(p => Grid.GetColumn(p)).ToArray();
        var placeholders = panels.Select(p => new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 29, 38)), BorderBrush = new SolidColorBrush(Color.FromRgb(63, 65, 84)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "Panel grouped in a tabbed window", Foreground = new SolidColorBrush(Color.FromRgb(166, 170, 186)),
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap }
        }).ToArray();
        foreach (var (panel, placeholder) in panels.Zip(placeholders))
        {
            int at = WorkspaceGrid.Children.IndexOf(panel);
            WorkspaceGrid.Children.Remove(panel);
            WorkspaceGrid.Children.Insert(Math.Clamp(at, 0, WorkspaceGrid.Children.Count), placeholder);
            Grid.SetColumn(placeholder, Grid.GetColumn(panel));
        }
        var tabs = new TabControl { Background = new SolidColorBrush(Color.FromRgb(24, 25, 34)), BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = sourceId, Content = source });
        tabs.Items.Add(new TabItem { Header = targetId, Content = target });
        var window = new Window { Title = "MyEditor · Panel Group", Owner = this, Width = 900, Height = 600, MinWidth = 500, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(20, 21, 29)), Content = tabs };
        var state = new DockGroupState(window, panels, placeholders, columns);
        _floatingGroups.Add(state);
        window.Closed += (_, _) => RestoreDockGroup(state, closeWindow: false);
        window.Show();
        Status.Text = $"{sourceId} and {targetId} grouped in tabs";
    }

    void RestoreDockGroup(DockGroupState state, bool closeWindow)
    {
        if (!_floatingGroups.Remove(state)) return;
        if (state.Window.Content is TabControl tabs) tabs.Items.Clear();
        state.Window.Content = null;
        for (int i = 0; i < state.Panels.Length; i++)
        {
            var placeholder = state.Placeholders[i];
            int index = WorkspaceGrid.Children.IndexOf(placeholder);
            if (index >= 0) WorkspaceGrid.Children.RemoveAt(index);
            WorkspaceGrid.Children.Insert(Math.Clamp(index, 0, WorkspaceGrid.Children.Count), state.Panels[i]);
            Grid.SetColumn(state.Panels[i], state.Columns[i]);
        }
        if (closeWindow && state.Window.IsVisible) state.Window.Close();
        if (closeWindow) Status.Text = "Tabbed panel group docked";
    }

    void RestoreWorkspacePanel(string id, bool closeWindow)
    {
        if (!_floatingPanels.Remove(id, out var state)) return;
        if (state.Placeholder.Parent is Panel parent)
        {
            int index = parent.Children.IndexOf(state.Placeholder);
            parent.Children.Remove(state.Placeholder);
            state.Window.Content = null;
            int insertAt = Math.Clamp(index >= 0 ? index : state.ChildIndex, 0, parent.Children.Count);
            parent.Children.Insert(insertAt, state.Panel);
            Grid.SetRow(state.Panel, Grid.GetRow(state.Placeholder)); Grid.SetColumn(state.Panel, Grid.GetColumn(state.Placeholder));
        }
        if (closeWindow && state.Window.IsVisible) state.Window.Close();
        if (closeWindow) Status.Text = $"{state.Title} docked";
    }

    void ApplyWorkspacePreset(string name, bool persist, bool applyLayout = true)
    {
        if (WorkspaceGrid.ActualWidth < 500 || _applyingWorkspacePreset) return;
        _applyingWorkspacePreset = true;
        try
        {
            // These presets rearrange the editor's existing panels and select the most useful
            // asset shelf for each task; all splitters remain user-adjustable and are autosaved.
            var (shelf, media, inspector, upper) = name switch
            {
                "Effects" => (3, .28, .24, .64),
                "Assembly" => (0, .25, .17, .54),
                "Audio" => (0, .22, .29, .54),
                "Graphics" => (5, .27, .22, .58),
                "Editing" => (0, .31, .22, .62),
                _ => (0, .32, .22, .62)
            };
            _workspacePreset = name is "Editing" or "Effects" or "Assembly" or "Audio" or "Graphics" or "Classic" ? name : "Classic";
            WorkspaceTabs.SelectedIndex = shelf;
            if (applyLayout)
            {
                double available = Math.Max(500, WorkspaceGrid.ActualWidth - 24);
                MediaPaneColumn.Width = new GridLength(Math.Clamp(available * media, MediaPaneColumn.MinWidth, available * .55));
                InspectorPaneColumn.Width = new GridLength(Math.Clamp(available * inspector, InspectorPaneColumn.MinWidth, available * .4));
                UpperWorkspaceRow.Height = new GridLength(upper, GridUnitType.Star);
                TimelineWorkspaceRow.Height = new GridLength(1 - upper, GridUnitType.Star);
            }
            if (persist)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WorkspacePresetPath)!);
                    File.WriteAllText(WorkspacePresetPath, _workspacePreset);
                }
                catch { }
                SaveWorkspaceLayout();
                Status.Text = $"{_workspacePreset} workspace";
            }
        }
        finally { _applyingWorkspacePreset = false; }
    }

    bool HasSelection => _selected != null || _selOv != null || _selAu != null || _multiSelection.Count > 0;

    async Task SetupMediaToolsAsync()
    {
        if (FFmpegService.ToolsInstalled) { Status.Text = "Ready"; _ = ShowEngineSummaryAsync(); return; }
        await EnsureMediaToolsInteractive();
    }

    async Task<bool> EnsureMediaToolsInteractive()
    {
        SetupBtn.IsEnabled = false;
        Status.Text = "Setting up media tools…";
        ExportBar.Visibility = Visibility.Visible;
        ExportBar.IsIndeterminate = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                ExportBar.IsIndeterminate = false;
                ExportBar.Value = p * 100;
                Status.Text = $"Downloading setup {p:P0}";
            });
            await RuntimeModuleService.EnsureAsync(RuntimeModule.MediaEngine, progress);
            Status.Text = "Ready";
            _ = ShowEngineSummaryAsync();
            SetupBtn.Visibility = Visibility.Collapsed;
            _ = RefreshEncoderAvailabilityAsync();
            return true;
        }
        catch (Exception ex)
        {
            Status.Text = "Setup required";
            SetupBtn.Visibility = Visibility.Visible;
            var logFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyEditor");
            try
            {
                System.IO.Directory.CreateDirectory(logFolder);
                await System.IO.File.AppendAllTextAsync(System.IO.Path.Combine(logFolder, "setup.log"), $"{DateTime.Now:u}\n{ex}\n\n");
            }
            catch { }
            MessageBox.Show(this,
                "Media tools could not be set up. Check the internet connection, then click Retry setup. The app tries a smaller 7-Zip download first and falls back automatically.\n\nDetails: " + ex.Message,
                "Media tools setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            SetupBtn.IsEnabled = true;
            ExportBar.IsIndeterminate = false;
            ExportBar.Visibility = Visibility.Collapsed;
        }
    }

    async Task LoadRecordingAudioDevicesAsync()
    {
        if (!FFmpegService.ToolsInstalled) return;
        string selected = (RecordingAudioBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        try
        {
            var devices = await ScreenRecorderService.ListAudioDevicesAsync();
            RecordingAudioBox.Items.Clear();
            RecordingAudioBox.Items.Add(new ComboBoxItem { Content = "Record audio: Off", Tag = "" });
            RecordingAudioBox.Items.Add(new ComboBoxItem { Content = "System audio · WASAPI loopback", Tag = RecorderAudioSource.WasapiLoopback, ToolTip = "Capture the sound playing from the default Windows output device." });
            foreach (var device in devices)
                RecordingAudioBox.Items.Add(new ComboBoxItem { Content = "Audio: " + device, Tag = device, ToolTip = device });
            RecordingAudioBox.SelectedItem = RecordingAudioBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
                string.Equals(i.Tag as string, selected, StringComparison.OrdinalIgnoreCase)) ?? RecordingAudioBox.Items[0];
            if (devices.Count == 0)
                RecordingAudioBox.ToolTip = "System audio uses WASAPI loopback. Microphone inputs may need Windows microphone privacy permission.";
        }
        catch { /* Keep video-only recording available when the bundled FFmpeg cannot enumerate audio devices. */ }
    }

    async void SetupTools_Click(object sender, RoutedEventArgs e) => await EnsureMediaToolsInteractive();

    void RecordingModeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item || !int.TryParse(tag, out int mode)) return;
        RecordingModeBox.SelectedIndex = Math.Clamp(mode, 0, 2);
        RecordingLiteMenuItem.IsChecked = mode == 0;
        RecordingExtraLiteMenuItem.IsChecked = mode == 1;
        Recording60MenuItem.IsChecked = mode == 2;
    }

    void TrackMouseMenu_Click(object sender, RoutedEventArgs e)
    {
        RecordMouseCheck.IsChecked = TrackMouseMenuItem.IsChecked;
    }

    void TrackTypingMenu_Click(object sender, RoutedEventArgs e)
    {
        RecordTypingCheck.IsChecked = TrackTypingMenuItem.IsChecked;
        _recorderOptions.AutoZoomOnTyping = TrackTypingMenuItem.IsChecked;
        _recorderOptions.Save();
    }

    void LowMemoryMenu_Click(object sender, RoutedEventArgs e)
    {
        ProxyModeCheck.IsChecked = LowMemoryMenuItem.IsChecked;
    }

    /// <summary>Enable only the encoders that this PC's FFmpeg actually contains.</summary>
    async Task RefreshEncoderAvailabilityAsync()
    {
        try
        {
            _availableEncoders = await FFmpegService.GetAvailableEncodersAsync();
            foreach (ComboBoxItem item in EncoderBox.Items)
                item.IsEnabled = item.Tag is string id && _availableEncoders.Contains(id);
            if (EncoderBox.SelectedItem is ComboBoxItem { IsEnabled: false }) EncoderBox.SelectedIndex = 0;
            if (IsLoaded) ApplyPerformanceProfile();
        }
        catch { /* leave the default encoder selected */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetMemoryStatus(ref MemoryStatusEx status);

    static double InstalledRamGiB()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GetMemoryStatus(ref status) ? status.TotalPhysical / 1073741824.0 : 0;
    }

    void PerformanceMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyPerformanceProfile();
    }

    void QuickSetupAuto_Click(object sender, RoutedEventArgs e)
    {
        PerformanceModeBox.SelectedIndex = 0;
        ApplyPerformanceProfile();
        Status.Text = "Recommended settings applied for this PC";
        _projectDirty = true;
    }

    void QuickSetupLite_Click(object sender, RoutedEventArgs e)
    {
        PerformanceModeBox.SelectedIndex = 1;
        ApplyPerformanceProfile();
        Status.Text = "Low-end PC settings applied · smoother preview and export";
        _projectDirty = true;
    }

    void QuickSetupQuality_Click(object sender, RoutedEventArgs e)
    {
        PerformanceModeBox.SelectedIndex = 2;
        ApplyPerformanceProfile();
        VideoQualityBox.SelectedIndex = VideoQualityBox.Items.Count - 1;
        AudioQualityBox.SelectedIndex = AudioQualityBox.Items.Count - 1;
        Status.Text = "Higher quality settings applied · 1080p30, best available encoder";
        _projectDirty = true;
    }

    void ApplyPerformanceProfile()
    {
        if (PerformanceModeBox == null || EncoderBox == null || _applyingPerformanceProfile) return;
        _applyingPerformanceProfile = true;
        bool wasLoading = _loading, wasDirty = _projectDirty;
        bool lite = false;
        _loading = true;
        try
        {
            double ram = InstalledRamGiB();
            // An 8 GB Windows PC has little memory left for a 1080p editor + export process.
            // Keep Auto conservative until the machine has 10 GB of physical RAM.
            lite = PerformanceModeBox.SelectedIndex == 1 || (PerformanceModeBox.SelectedIndex == 0 && (ram <= 0 || ram < 10.0));
            bool high = !lite;
            string? gpu = new[] { "h264_nvenc", "h264_amf", "h264_qsv" }.FirstOrDefault(_availableEncoders.Contains);

            QualityBox.SelectedIndex = lite ? 0 : 2; // 540p24 CPU-safe, or 1080p30
            if (lite)
            {
                EncoderBox.SelectedIndex = 0;
                RecordingModeBox.SelectedIndex = 1;
                ProxyModeCheck.IsChecked = true;
            }
            else
            {
                RecordingModeBox.SelectedIndex = 0;
                if (gpu != null)
                    EncoderBox.SelectedItem = EncoderBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, gpu));
                else if (_availableEncoders.Contains("h264_mf"))
                    EncoderBox.SelectedItem = EncoderBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, "h264_mf"));
                else EncoderBox.SelectedIndex = 0;
            }
            RecordingLiteMenuItem.IsChecked = RecordingModeBox.SelectedIndex == 0;
            RecordingExtraLiteMenuItem.IsChecked = RecordingModeBox.SelectedIndex == 1;
            Recording60MenuItem.IsChecked = RecordingModeBox.SelectedIndex == 2;
            TrackMouseMenuItem.IsChecked = RecordMouseCheck.IsChecked == true;
            TrackTypingMenuItem.IsChecked = RecordTypingCheck.IsChecked == true;
            LowMemoryMenuItem.IsChecked = ProxyModeCheck.IsChecked == true;

            string ramText = ram > 0 ? $"{ram:0.#} GB RAM" : "RAM unknown";
            PerformanceInfo.Text = lite
                ? $"Lite · {ramText} · CPU"
                : high && gpu != null ? $"High · {ramText} · GPU ready" : $"High · {ramText} · CPU";
            PerformanceInfo.ToolTip = lite
                ? "Lite uses 540p/12fps recording, 540p/24fps export, cached preview proxies, one software decoder/filter/encoder thread and below-normal export priority to reduce RAM and keep the editor responsive."
                : "High selects 1080p/30fps export, automatic hardware decoding where available, and a compatible GPU encoder when FFmpeg provides one; otherwise CPU H.264 is used.";
        }
        finally
        {
            _loading = wasLoading;
            _projectDirty = wasDirty;
            _applyingPerformanceProfile = false;
        }
        if (IsLoaded) Status.Text = lite ? "Lite profile ready · low-memory settings" : "High profile ready · 10 GB+ settings";
    }

    string SelectedEncoder => (EncoderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "h264";
    int SelectedAudioKbps => int.TryParse((AudioQualityBox.SelectedItem as ComboBoxItem)?.Tag as string, out int value) ? value : 192;

    void OpenVideoLab_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdvancedVideoSettingsWindow(_backgroundMode, _backgroundColor, _backgroundImagePath) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _backgroundMode = dialog.BackgroundMode;
        _backgroundColor = dialog.BackgroundColor;
        _backgroundImagePath = dialog.BackgroundImagePath;
        PreviewHost.Background = PreviewBackgroundBrush();
        _loading = true; SyncQuickBackgroundBox(); _loading = false;
        ShowFrame(false);
        _projectDirty = true;
        Status.Text = $"Video Lab settings applied · {_backgroundMode}";
    }

    void SyncQuickBackgroundBox()
    {
        if (QuickBackgroundBox == null) return;
        string tag = _backgroundMode switch
        {
            "Soft blur fill" => "Soft blur fill",
            "Custom image" => "Custom image",
            "Black bars" => "Black bars",
            _ => _backgroundColor is "#151820" or "#33234A" or "#12363A" ? _backgroundColor : "Solid color"
        };
        QuickBackgroundBox.SelectedItem = QuickBackgroundBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? QuickBackgroundBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == "#151820");
    }

    void QuickBackground_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QuickBackgroundBox?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (tag == "Custom image")
        {
            if (!File.Exists(_backgroundImagePath))
            {
                OpenVideoLab_Click(this, new RoutedEventArgs());
                _loading = true; SyncQuickBackgroundBox(); _loading = false;
                return;
            }
            _backgroundMode = "Custom image";
        }
        else if (tag == "Solid color")
        {
            OpenVideoLab_Click(this, new RoutedEventArgs());
            _loading = true; SyncQuickBackgroundBox(); _loading = false;
            return;
        }
        else if (tag.StartsWith('#'))
        {
            _backgroundMode = "Solid color";
            _backgroundColor = tag;
        }
        else if (tag == "Black bars")
        {
            _backgroundMode = "Black bars";
            _backgroundColor = "#151820";
        }
        else _backgroundMode = tag;
        PreviewHost.Background = PreviewBackgroundBrush();
        ShowFrame(false);
        _projectDirty = true;
        Status.Text = $"Background applied · {_backgroundMode}";
    }

    void OpenEffectsBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            MessageBox.Show(this, "Select a video clip on the V1 timeline first.", "Select a video clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var browser = new EffectsBrowserWindow { Owner = this };
        if (browser.ShowDialog() != true) return;
        Pause();
        _loading = true;
        if (browser.SelectedEffect is { } effect) SelectCombo(FilterBox, effect);
        if (browser.SelectedTransition is { } transition) SelectCombo(TransitionBox, transition);
        _loading = false;
        ApplyParams(true);
        Status.Text = browser.SelectedEffect is { } appliedEffect
            ? $"Applied effect · {appliedEffect}"
            : $"Applied transition · {browser.SelectedTransition}";
    }

    void OpenStickers_Click(object sender, RoutedEventArgs e)
    {
        var browser = new StickerBrowserWindow { Owner = this };
        if (browser.ShowDialog() != true || browser.SelectedSticker is not { } sticker) return;
        Pause();
        string path;
        try { path = StickerCatalog.CreateAsset(sticker); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create the sticker: " + ex.Message, "Sticker error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Checkpoint(); ClearSelection();
        var overlay = new OverlayClip
        {
            Path = path, IsImage = true, Start = _playhead, In = 0, Duration = 3, SourceDuration = 3,
            AspectRatio = 1, X = 0.5, Y = 0.5, Scale = 0.24, Opacity = 1,
            AnimationPreset = sticker.AnimationPreset, VideoTrack = 2
        };
        _overlays.Add(overlay); _selOv = overlay; AfterEdit();
        Status.Text = $"Sticker added · {sticker.Name}";
    }

    // ---------- premium shapes & backgrounds (Design Studio) ----------
    void OpenDesignStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_designStudio != null) { _designStudio.Activate(); return; }
        _designStudio = new DesignStudioWindow(() => Math.Clamp(AspectBox.SelectedIndex, 0, 2)) { Owner = this };
        _designStudio.ActionRequested += (design, action) =>
        {
            if (action == DesignAction.SetVideoBackground) SetDesignAsVideoBackground(design.Asset);
            else _ = PlaceDesignAsync(design, _playhead, null, null, design.Asset.Kind != DesignKind.Shape ? 0 : 2);
        };
        _designStudio.Closed += (_, _) => _designStudio = null;
        _designStudio.Show();
    }

    /// <param name="lane">0 = main track (background scene), 2–4 = overlay lane.</param>
    async Task PlaceDesignAsync(DesignDrag design, double time, double? x, double? y, int lane)
    {
        int aspect = Math.Clamp(AspectBox.SelectedIndex, 0, 2);
        bool animated = design.Asset.Kind == DesignKind.Animated;
        string path;
        try
        {
            if (animated)
            {
                Status.Text = $"Rendering animated background · {design.Asset.Name} (first time only)…";
                path = await DesignCatalog.CreateAnimatedAsync(design.Asset, aspect, design.Duration);
            }
            else path = DesignCatalog.CreateAsset(design.Asset, aspect);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create the design: " + ex.Message, "Design error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Pause(); Checkpoint(); ClearSelection();
        bool background = design.Asset.Kind != DesignKind.Shape;
        if (background && lane == 0)
        {
            var clip = new Clip { Path = path, SourceDuration = design.Duration, In = 0, Out = design.Duration, HasAudio = false, IsImage = !animated };
            int index = MainDropIndex(time + clip.Duration / 2, null);
            _timeline.Insert(Math.Clamp(index, 0, _timeline.Count), clip);
            _selected = clip;
        }
        else
        {
            double ratio = DesignCatalog.AspectOf(design.Asset, aspect);
            var overlay = new OverlayClip
            {
                Path = path, IsImage = !animated, Start = Math.Max(0, time), In = 0,
                Duration = design.Duration, SourceDuration = design.Duration, AspectRatio = ratio,
                X = background ? 0.5 : x ?? 0.5, Y = background ? 0.5 : y ?? 0.5,
                Scale = background ? 1.0 : ratio > 2.2 ? 0.5 : 0.24, Opacity = 1,
                AnimationPreset = design.Motion, VideoTrack = Math.Clamp(lane, 2, 4)
            };
            _overlays.Add(overlay); _selOv = overlay;
        }
        AfterEdit();
        Status.Text = $"Added {design.Asset.Name}";
    }

    void DropDesignOnTimeline(DesignDrag design, Point point)
    {
        if (point.X < TrackLabelWidth) return;
        double time = Math.Max(0, (point.X - TrackLabelWidth) / Pps);
        bool background = design.Asset.Kind != DesignKind.Shape;
        if (point.Y >= LaneMainY && point.Y < LaneOvY) _ = PlaceDesignAsync(design, time, null, null, background ? 0 : 2);
        else if (point.Y >= LaneOvY && point.Y < LaneTextY) _ = PlaceDesignAsync(design, time, null, null, VideoTrackAt(point.Y));
        else Status.Text = "Drop designs on V1 (background scene) or V2–V4 (overlay)";
    }

    void SetDesignAsVideoBackground(DesignAsset asset)
    {
        string path;
        try { path = DesignCatalog.CreateAsset(asset, Math.Clamp(AspectBox.SelectedIndex, 0, 2)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not create the background: " + ex.Message, "Design error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _backgroundMode = "Custom image";
        _backgroundImagePath = path;
        PreviewHost.Background = PreviewBackgroundBrush();
        _loading = true; SyncQuickBackgroundBox(); _loading = false;
        if (!_projectDirty) { _projectDirty = true; UpdateProjectTitle(); }
        Status.Text = $"Video background set · {asset.Name} (visible behind portrait/square video)";
    }

    void Preview_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DesignDrag)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Preview_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DesignDrag)) is not DesignDrag design) return;
        e.Handled = true;
        if (design.Asset.Kind == DesignKind.Background) { SetDesignAsVideoBackground(design.Asset); return; }
        if (design.Asset.Kind == DesignKind.Animated) { _ = PlaceDesignAsync(design, _playhead, null, null, 0); return; }
        Point p = e.GetPosition(PreviewHost);
        double bw = PreviewHost.ActualWidth, bh = PreviewHost.ActualHeight;
        if (bw < 20 || bh < 20) return;
        double fw = bw, fh = bw * 9 / 16;
        if (fh > bh) { fh = bh; fw = bh * 16 / 9; }
        double ox = (bw - fw) / 2, oy = (bh - fh) / 2;
        _ = PlaceDesignAsync(design, _playhead, Math.Clamp((p.X - ox) / fw, 0, 1), Math.Clamp((p.Y - oy) / fh, 0, 1), 2);
    }

    Brush PreviewBackgroundBrush()
    {
        if (_backgroundMode == "Custom image" && File.Exists(_backgroundImagePath))
        {
            try { return new ImageBrush(LoadImage(_backgroundImagePath)) { Stretch = Stretch.UniformToFill }; } catch { }
        }
        if (_backgroundMode is "Solid color" or "Black bars")
        {
            try { return (Brush)new BrushConverter().ConvertFromString(_backgroundColor)!; } catch { }
        }
        return new SolidColorBrush(_backgroundMode == "Soft blur fill" ? Color.FromRgb(0x25, 0x22, 0x35) : Color.FromRgb(0x08, 0x09, 0x0D));
    }

    // ---------- undo / helpers ----------
    static string Fmt(double s, bool frac = false) =>
        TimeSpan.FromSeconds(Math.Max(0, s)).ToString(frac ? @"mm\:ss\.ff" : @"mm\:ss");

    int TimelineFps => Presets[Math.Clamp(QualityBox?.SelectedIndex ?? 0, 0, Presets.Length - 1)].Fps;
    double SnapToFrame(double seconds) => Math.Round(Math.Max(0, seconds) * TimelineFps, MidpointRounding.AwayFromZero) / TimelineFps;
    string FrameTimecode(double seconds)
    {
        long frames = (long)Math.Round(Math.Max(0, seconds) * TimelineFps, MidpointRounding.AwayFromZero);
        long totalSeconds = frames / TimelineFps;
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}:{frames % TimelineFps:00}";
    }

    void UpdateTrimFrameInfo(Clip clip)
    {
        if (TrimFrameInfo == null) return;
        long frameCount = Math.Max(1, (long)Math.Round(clip.Duration * TimelineFps));
        TrimFrameInfo.Text = $"In {FrameTimecode(clip.In)}  ·  Out {FrameTimecode(clip.Out)}  ·  {frameCount} frames";
    }

    static List<Clip> CloneList(List<Clip> l) => l.Select(c => c.Clone()).ToList();

    const int MaxUndoSteps = 100;
    Snapshot Snap() => new(CloneList(_timeline), _overlays.Select(o => o.Clone()).ToList(), _audios.Select(a => a.Clone()).ToList(), _playhead, _textLaneCount, _audioLaneCount,
        new(_mutedTracks, StringComparer.OrdinalIgnoreCase), new(_lockedTracks, StringComparer.OrdinalIgnoreCase));

    void Restore(Snapshot s)
    {
        _timeline = s.Main; _overlays = s.Ov; _audios = s.Au;
        _textLaneCount = Math.Clamp(s.TextLaneCount, 1, MaxTimelineLanes); _audioLaneCount = Math.Clamp(s.AudioLaneCount, 1, MaxTimelineLanes);
        _mutedTracks.Clear(); foreach (var pair in s.MutedTracks) _mutedTracks[pair.Key] = pair.Value;
        _lockedTracks.Clear(); foreach (var pair in s.LockedTracks) _lockedTracks[pair.Key] = pair.Value;
        _playhead = Math.Clamp(s.Playhead, 0, Math.Max(Extent, 0));
        ClearSelection();
    }
    void ClearSelection(bool clearMulti = true)
    {
        _selected = null; _selOv = null; _selAu = null; _selectedZoomEvent = null; _zoomEventClip = null;
        if (clearMulti) _multiSelection.Clear();
    }

    bool TimelineItemSelected(object item, object? single) =>
        _multiSelection.Count > 0 ? _multiSelection.Contains(item) : ReferenceEquals(item, single);

    void FocusTimelineItem(object item)
    {
        _selected = item as Clip;
        _selOv = item as OverlayClip;
        _selAu = item as AudioClip;
        _selectedZoomEvent = null;
        _zoomEventClip = null;
    }

    void Checkpoint()
    {
        _undo.Push(Snap());
        if (_undo.Count > MaxUndoSteps)
        {
            var keep = _undo.Take(MaxUndoSteps).Reverse().ToArray();
            _undo.Clear();
            foreach (var step in keep) _undo.Push(step);
        }
        _redo.Clear();
        if (!_projectDirty) { _projectDirty = true; UpdateProjectTitle(); }
    }

    void AfterEdit()
    {
        if (!_projectDirty) { _projectDirty = true; UpdateProjectTitle(); }
        _playhead = Math.Min(_playhead, Extent);
        Refresh();
        LoadInspector();
        ShowFrame(false);
        SyncAudioPreview();
    }

    (Clip? clip, double srcTime, double start) Locate(double t)
    {
        double s = 0;
        for (int i = 0; i < _timeline.Count; i++)
        {
            var c = _timeline[i];
            bool last = i == _timeline.Count - 1;
            if (t < s + c.Duration || (last && t <= s + c.Duration))
                return (c, c.In + (t - s) * c.Speed, s);
            s += c.Duration;
        }
        return (null, 0, 0);
    }

    // ---------- inspector ----------
    static void SelectCombo(ComboBox b, string v)
    {
        if (b.ItemsSource is IEnumerable<string> strings)
        {
            b.SelectedItem = strings.FirstOrDefault(x => x == v) ?? strings.FirstOrDefault();
            return;
        }
        foreach (ComboBoxItem it in b.Items)
            if ((string?)it.Content == v) { b.SelectedItem = it; return; }
        b.SelectedIndex = 0;
    }

    static string ComboVal(ComboBox b) => (b.SelectedItem as ComboBoxItem)?.Content as string ?? b.SelectedItem as string ?? "None";

    void LoadInspector()
    {
        _loading = true;
        HintText.Visibility = HasSelection ? Visibility.Collapsed : Visibility.Visible;
        RefreshMotionPanel();
        Inspector.Visibility = _selected is { IsGap: false } ? Visibility.Visible : Visibility.Collapsed;
        GapPanel.Visibility = _selected is { IsGap: true } ? Visibility.Visible : Visibility.Collapsed;
        LayerPanel.Visibility = _selOv != null ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = _selAu != null ? Visibility.Visible : Visibility.Collapsed;

        if (_selected is { } c)
        {
            InspTitle.Text = c.Name;
            PropertiesTitle.Text = c.IsGap ? "Properties — Timeline gap" : "Properties — " + c.Name;
            GapDurationSlider.Value = c.IsGap ? Math.Clamp(c.GapDuration, GapDurationSlider.Minimum, GapDurationSlider.Maximum) : GapDurationSlider.Minimum;
            double frameDuration = 1.0 / TimelineFps;
            TrimInSlider.Maximum = Math.Max(0, c.Out - frameDuration);
            TrimOutSlider.Maximum = Math.Max(0.1, c.SourceDuration);
            TrimOutSlider.Minimum = Math.Min(c.SourceDuration, c.In + frameDuration);
            TrimInSlider.Value = Math.Clamp(c.In, TrimInSlider.Minimum, TrimInSlider.Maximum);
            TrimOutSlider.Value = Math.Clamp(c.Out, TrimOutSlider.Minimum, TrimOutSlider.Maximum);
            UpdateTrimFrameInfo(c);
            MainFrameScaleSlider.Value = Math.Clamp(c.FrameScale, MainFrameScaleSlider.Minimum, MainFrameScaleSlider.Maximum);
            MainFrameXSlider.Value = Math.Clamp(c.FrameX, MainFrameXSlider.Minimum, MainFrameXSlider.Maximum);
            MainFrameYSlider.Value = Math.Clamp(c.FrameY, MainFrameYSlider.Minimum, MainFrameYSlider.Maximum);
            SyncQuickBackgroundBox();
            SpeedSlider.Value = c.Speed;
            BrightnessSlider.Value = c.Brightness;
            ContrastSlider.Value = c.Contrast;
            SaturationSlider.Value = c.Saturation;
            TemperatureSlider.Value = c.Temperature;
            TintSlider.Value = c.Tint;
            ExposureSlider.Value = c.Exposure;
            HighlightsSlider.Value = c.Highlights;
            ShadowsSlider.Value = c.Shadows;
            HueSlider.Value = c.Hue;
            VibranceSlider.Value = c.Vibrance;
            SharpnessSlider.Value = c.Sharpness;
            VignetteSlider.Value = c.Vignette;
            VolumeSlider.Value = c.Volume;
            StabilizeCheck.IsChecked = c.Stabilize;
            DenoiseCheck.IsChecked = c.DenoiseAudio;
            SelectCombo(FilterBox, c.Filter);
            SelectCombo(VoiceBox, c.Voice);
            SelectCombo(TransitionBox, c.TransitionIn);
            OverlayBox.Text = c.OverlayText;
            MouseZoomCheck.Visibility = c.PointerEvents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MouseZoomCheck.IsChecked = c.AutoMouseZoom;
            ClickEffectsPanel.Visibility = c.PointerEvents.Any(p => p.IsClick) ? Visibility.Visible : Visibility.Collapsed;
            ClickEffectsCheck.IsChecked = c.ShowClickEffects;
            ClickEffectSizeSlider.Value = Math.Clamp(c.ClickEffectSize, ClickEffectSizeSlider.Minimum, ClickEffectSizeSlider.Maximum);
            ClickEffectDurationSlider.Value = Math.Clamp(c.ClickEffectDuration, ClickEffectDurationSlider.Minimum, ClickEffectDurationSlider.Maximum);
            ClickEffectColorBox.SelectedItem = ClickEffectColorBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, c.ClickEffectColor)) ?? ClickEffectColorBox.Items[0];
            ClickEffectStyleBox.SelectedItem = ClickEffectStyleBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, c.ClickEffectStyle)) ?? ClickEffectStyleBox.Items[0];
            MouseZoomControls.Visibility = _selectedZoomEvent != null ? Visibility.Visible : Visibility.Collapsed;
            MouseZoomStrengthSlider.Value = Math.Clamp(_selectedZoomEvent?.ZoomStrength ?? 1.35, 1, 2);
            MouseZoomSpeedSlider.Value = Math.Clamp(_selectedZoomEvent?.ZoomSpeedSeconds ?? 0.22, 0.05, 1);
            MouseZoomDurationSlider.Value = Math.Clamp(_selectedZoomEvent?.ZoomDurationSeconds ?? 1.07, 0.2, 4);
            SelectCombo(ZoomEasingBox, _selectedZoomEvent?.ZoomEasing ?? "Smooth");
        }
        if (_selOv is { } o)
        {
            LayerTitle.Text = o.IsText ? "Text clip" : "Overlay: " + o.Name;
            PropertiesTitle.Text = "Properties — " + LayerTitle.Text;
            LDurSlider.Value = o.Duration;
            AnimationBox.SelectedItem = o.AnimationPreset;
            if (AnimationBox.SelectedItem == null) AnimationBox.SelectedIndex = 0;
            LXSlider.Value = o.X;
            LYSlider.Value = o.Y;
            LMediaOnly.Visibility = o.IsText ? Visibility.Collapsed : Visibility.Visible;
            LTextOnly.Visibility = o.IsText ? Visibility.Visible : Visibility.Collapsed;
            LScaleSlider.Value = o.Scale;
            LOpacitySlider.Value = o.Opacity;
            LKeyInfo.Text = $"Keyframes: {o.Keyframes.Count} (need 2 or more to animate)";
            LTextBox.Text = o.Text;
            LFontSlider.Value = o.FontSize;
            LStartBox.Text = o.Start.ToString("0.###", CultureInfo.InvariantCulture);
            SelectCombo(LFontFamilyBox, o.FontFamily);
            LTextBoldCheck.IsChecked = o.Bold; LTextItalicCheck.IsChecked = o.Italic;
            SelectCombo(LTextAlignmentBox, o.TextAlignment);
            LLineSpacingSlider.Value = Math.Clamp(o.LineSpacing, LLineSpacingSlider.Minimum, LLineSpacingSlider.Maximum);
            SelectCombo(LColorBox, o.Color);
            SelectCombo(LShapeBox, o.TextShape);
            TextShadowCheck.IsChecked = o.TextShadow;
            TextOutlineSlider.Value = Math.Clamp(o.TextOutlineWidth, TextOutlineSlider.Minimum, TextOutlineSlider.Maximum);
            SelectCombo(TextOutlineColorBox, o.TextOutlineColor);
            SelectCombo(TextShadowColorBox, o.TextShadowColor);
            TextShadowSoftnessSlider.Value = Math.Clamp(o.TextShadowSoftness, TextShadowSoftnessSlider.Minimum, TextShadowSoftnessSlider.Maximum);
            TextOpacitySlider.Value = Math.Clamp(o.TextOpacity, TextOpacitySlider.Minimum, TextOpacitySlider.Maximum);
            LTextKeyInfo.Text = $"Text keyframes: {o.Keyframes.Count} · move the playhead and change position, size, or opacity to animate";
            if (o.Keyframes.Count > 0)
            {
                var keyed = o.AtText(Math.Clamp(_playhead - o.Start, 0, o.Duration));
                LXSlider.Value = keyed.x; LYSlider.Value = keyed.y;
                LFontSlider.Value = Math.Clamp(o.FontSize * keyed.s, LFontSlider.Minimum, LFontSlider.Maximum);
                TextOpacitySlider.Value = Math.Clamp(o.TextOpacity * keyed.opacity, TextOpacitySlider.Minimum, TextOpacitySlider.Maximum);
            }
        }
        if (_selAu is { } a)
        {
            ATitle.Text = "Audio: " + a.Name;
            PropertiesTitle.Text = "Properties — " + a.Name;
            ADurSlider.Maximum = Math.Max(0.5, a.SourceDuration);
            ADurSlider.Value = Math.Min(a.Duration, ADurSlider.Maximum);
            AVolumeSlider.Value = Math.Clamp(a.VolumeAt(Math.Clamp(_playhead - a.Start, 0, a.Duration)), AVolumeSlider.Minimum, AVolumeSlider.Maximum);
            AFadeInSlider.Maximum = Math.Min(5, Math.Max(0, a.Duration / 2));
            AFadeOutSlider.Maximum = Math.Min(5, Math.Max(0, a.Duration / 2));
            AFadeInSlider.Value = Math.Clamp(a.FadeInSeconds, AFadeInSlider.Minimum, AFadeInSlider.Maximum);
            AFadeOutSlider.Value = Math.Clamp(a.FadeOutSeconds, AFadeOutSlider.Minimum, AFadeOutSlider.Maximum);
            AudioKeyInfo.Text = $"Volume keyframes: {a.VolumeKeyframes.Count} · move the playhead and adjust volume to animate";
        }
        _loading = false;
    }

    void ApplyParams(bool checkpoint)
    {
        if (_loading || _selected == null) return;
        if (IsObjectLocked(_selected)) { Status.Text = "Unlock V1 before changing this clip"; return; }
        if (checkpoint) Checkpoint();
        var c = _selected;
        c.Speed = SpeedSlider.Value;
        c.FrameScale = Math.Clamp(MainFrameScaleSlider.Value, 0.2, 2.0);
        c.FrameX = Math.Clamp(MainFrameXSlider.Value, 0, 1);
        c.FrameY = Math.Clamp(MainFrameYSlider.Value, 0, 1);
        c.Brightness = BrightnessSlider.Value;
        c.Contrast = ContrastSlider.Value;
        c.Saturation = SaturationSlider.Value;
        c.Temperature = TemperatureSlider.Value;
        c.Tint = TintSlider.Value;
        c.Exposure = ExposureSlider.Value;
        c.Highlights = HighlightsSlider.Value;
        c.Shadows = ShadowsSlider.Value;
        c.Hue = HueSlider.Value;
        c.Vibrance = VibranceSlider.Value;
        c.Sharpness = SharpnessSlider.Value;
        c.Vignette = VignetteSlider.Value;
        c.Volume = VolumeSlider.Value;
        c.Stabilize = StabilizeCheck.IsChecked == true;
        c.DenoiseAudio = DenoiseCheck.IsChecked == true;
        c.Filter = ComboVal(FilterBox);
        c.Voice = ComboVal(VoiceBox);
        c.TransitionIn = ComboVal(TransitionBox);
        c.OverlayText = OverlayBox.Text ?? "";
        c.AutoMouseZoom = MouseZoomCheck.IsChecked == true;
        c.ShowClickEffects = ClickEffectsCheck.IsChecked == true;
        c.ClickEffectColor = (ClickEffectColorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#61D9FF";
        c.ClickEffectStyle = (ClickEffectStyleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Soft click pulse";
        c.ClickEffectDuration = ClickEffectDurationSlider.Value;
        c.ClickEffectSize = ClickEffectSizeSlider.Value;
        if (_selectedZoomEvent != null)
        {
            _selectedZoomEvent.ZoomStrength = MouseZoomStrengthSlider.Value;
            _selectedZoomEvent.ZoomSpeedSeconds = MouseZoomSpeedSlider.Value;
            _selectedZoomEvent.ZoomDurationSeconds = MouseZoomDurationSlider.Value;
            _selectedZoomEvent.ZoomEasing = ComboVal(ZoomEasingBox);
        }

        _playhead = Math.Min(_playhead, Extent);
        if (Locate(_playhead).clip == c)
        {
            UpdateMainFrameTransform(c);
            Preview.SpeedRatio = c.Speed;
            Preview.Volume = IsTrackMuted("V1") ? 0 : Math.Min(1, c.Volume);
        }
        Refresh();
        var (previewClip, previewSourceTime, _) = Locate(_playhead);
        if (ReferenceEquals(previewClip, c)) ScheduleLiveGradePreview(c, previewSourceTime);
    }

    void ApplyLayer(bool checkpoint)
    {
        if (_loading || _selOv == null) return;
        if (IsObjectLocked(_selOv)) { Status.Text = "Unlock this track before changing its clip"; return; }
        if (checkpoint) Checkpoint();
        var o = _selOv;
        o.Duration = LDurSlider.Value;
        o.AnimationPreset = AnimationBox.SelectedItem as string ?? "None";
        if (o.IsText)
        {
            o.Text = LTextBox.Text ?? "";
            bool hasTextKeys = o.Keyframes.Count > 0;
            bool editTextKey = _textKeyEdit && hasTextKeys && _playhead >= o.Start && _playhead <= o.End;
            if (editTextKey)
            {
                double localTime = Math.Clamp(_playhead - o.Start, 0, o.Duration);
                var key = o.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - localTime) < 0.05);
                if (key == null)
                {
                    var current = o.AtText(localTime);
                    key = new Keyframe { Time = localTime, X = current.x, Y = current.y, Scale = current.s, Opacity = current.opacity };
                    o.Keyframes.Add(key);
                }
                key.X = LXSlider.Value; key.Y = LYSlider.Value;
                key.Scale = Math.Clamp(LFontSlider.Value / Math.Max(1, o.FontSize), 0.1, 5);
                key.Opacity = Math.Clamp(TextOpacitySlider.Value / Math.Max(0.1, o.TextOpacity), 0, 1);
                o.Keyframes = o.Keyframes.OrderBy(k => k.Time).ToList();
            }
            else if (!hasTextKeys)
            {
                o.X = LXSlider.Value; o.Y = LYSlider.Value;
                o.FontSize = (int)LFontSlider.Value;
                o.TextOpacity = TextOpacitySlider.Value;
            }
            o.FontFamily = ComboVal(LFontFamilyBox);
            o.Bold = LTextBoldCheck.IsChecked == true; o.Italic = LTextItalicCheck.IsChecked == true;
            o.TextAlignment = ComboVal(LTextAlignmentBox);
            o.LineSpacing = LLineSpacingSlider.Value;
            o.Color = ComboVal(LColorBox);
            o.TextShape = ComboVal(LShapeBox);
            o.TextShadow = TextShadowCheck.IsChecked == true;
            o.TextOutlineWidth = TextOutlineSlider.Value;
            o.TextOutlineColor = ComboVal(TextOutlineColorBox);
            o.TextShadowColor = ComboVal(TextShadowColorBox);
            o.TextShadowSoftness = TextShadowSoftnessSlider.Value;
        }
        else
        {
            o.X = LXSlider.Value; o.Y = LYSlider.Value;
            o.Scale = LScaleSlider.Value;
            o.Opacity = LOpacitySlider.Value;
        }
        Refresh();
    }

    void ApplyAudio(bool checkpoint)
    {
        if (_loading || _selAu == null) return;
        if (IsObjectLocked(_selAu)) { Status.Text = "Unlock this audio track before changing its clip"; return; }
        if (checkpoint) Checkpoint();
        if (_audioKeyEdit && _selAu.VolumeKeyframes.Count > 0 && _playhead >= _selAu.Start && _playhead <= _selAu.Start + _selAu.Duration)
        {
            double local = Math.Clamp(_playhead - _selAu.Start, 0, _selAu.Duration);
            var key = _selAu.VolumeKeyframes.FirstOrDefault(k => Math.Abs(k.Time - local) < 0.05);
            if (key == null) { key = new AudioVolumeKeyframe { Time = local, Volume = _selAu.VolumeAt(local) }; _selAu.VolumeKeyframes.Add(key); }
            key.Volume = AVolumeSlider.Value;
            _selAu.VolumeKeyframes = _selAu.VolumeKeyframes.OrderBy(k => k.Time).ToList();
        }
        else if (_selAu.VolumeKeyframes.Count == 0) _selAu.Volume = AVolumeSlider.Value;
        _selAu.Duration = ADurSlider.Value;
        _selAu.FadeInSeconds = Math.Min(AFadeInSlider.Value, _selAu.Duration / 2);
        _selAu.FadeOutSeconds = Math.Min(AFadeOutSlider.Value, _selAu.Duration / 2);
        Refresh();
    }

    static double EaseZoom(double u, string curve)
    {
        u = Math.Clamp(u, 0, 1);
        return curve switch
        {
            "Linear" => u,
            "Cinematic" => u * u * u * (u * (u * 6 - 15) + 10),
            "Snappy" => 1 - Math.Pow(1 - u, 3),
            _ => u * u * (3 - 2 * u)
        };
    }

    void Param_Slider(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyParams(false);
    void Trim_Slider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _selected is not { } clip) return;
        _loading = true;
        if (sender == TrimInSlider)
        {
            double frameDuration = 1.0 / TimelineFps;
            clip.In = Math.Clamp(SnapToFrame(TrimInSlider.Value), 0, Math.Max(0, clip.Out - frameDuration));
            TrimInSlider.Value = clip.In;
            TrimOutSlider.Minimum = Math.Min(clip.SourceDuration, clip.In + frameDuration);
            if (clip.Out < TrimOutSlider.Minimum) clip.Out = TrimOutSlider.Minimum;
            TrimOutSlider.Value = clip.Out;
        }
        else
        {
            double frameDuration = 1.0 / TimelineFps;
            clip.Out = Math.Clamp(SnapToFrame(TrimOutSlider.Value), Math.Min(clip.SourceDuration, clip.In + frameDuration), Math.Max(frameDuration, clip.SourceDuration));
            TrimOutSlider.Value = clip.Out;
        }
        _loading = false;
        UpdateTrimFrameInfo(clip);
        AfterEdit();
    }
    void Param_Combo(object sender, SelectionChangedEventArgs e) => ApplyParams(true);
    void Param_Check(object sender, RoutedEventArgs e) => ApplyParams(true);
    void Param_Text(object sender, RoutedEventArgs e) => ApplyParams(true);
    void Layer_Slider(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyLayer(false);
    void Layer_TransformSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _textKeyEdit = _selOv is { IsText: true };
        ApplyLayer(false);
        _textKeyEdit = false;
    }
    void TextKeySlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _textKeyEdit = _selOv is { IsText: true };
        ApplyLayer(false);
        _textKeyEdit = false;
    }
    void Layer_Combo(object sender, SelectionChangedEventArgs e) => ApplyLayer(true);
    void Animation_Changed(object sender, SelectionChangedEventArgs e) => ApplyLayer(true);
    void Layer_Text(object sender, RoutedEventArgs e)
    {
        bool checkpoint = !_textTypingUndoRecorded;
        _textTypingUndoRecorded = false;
        ApplyLayer(checkpoint);
    }
    void Layer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _selOv is not { IsText: true } text) return;
        if (!_textTypingUndoRecorded) { Checkpoint(); _textTypingUndoRecorded = true; }
        text.Text = LTextBox.Text;
        UpdateGuide();
    }
    void TextStart_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _selOv is not { IsText: true } text || !double.TryParse(LStartBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double start)) return;
        start = Math.Max(0, start);
        if (Math.Abs(start - text.Start) < 0.001) return;
        Pause(); Checkpoint(); text.Start = start; AfterEdit();
    }
    void TextAdvancedCheck_Click(object sender, RoutedEventArgs e) => ApplyLayer(true);
    void Audio_Slider(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAudio(false);
    void Audio_VolumeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioKeyEdit = _selAu is { VolumeKeyframes.Count: > 0 } && _playhead >= _selAu.Start && _playhead <= _selAu.Start + _selAu.Duration;
        ApplyAudio(false);
        _audioKeyEdit = false;
    }

    void AddAudioKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selAu == null) { Status.Text = "Select an audio clip first"; return; }
        if (IsObjectLocked(_selAu)) { Status.Text = "Unlock this audio track before editing it"; return; }
        if (_playhead < _selAu.Start || _playhead > _selAu.Start + _selAu.Duration) { Status.Text = "Move the playhead inside the audio clip to add a volume keyframe"; return; }
        Checkpoint();
        double local = Math.Clamp(_playhead - _selAu.Start, 0, _selAu.Duration);
        var key = _selAu.VolumeKeyframes.FirstOrDefault(k => Math.Abs(k.Time - local) < 0.05);
        if (key == null) _selAu.VolumeKeyframes.Add(key = new AudioVolumeKeyframe { Time = local, Volume = _selAu.VolumeAt(local) });
        key.Volume = AVolumeSlider.Value;
        _selAu.VolumeKeyframes = _selAu.VolumeKeyframes.OrderBy(k => k.Time).ToList();
        Refresh(); LoadInspector(); Status.Text = $"Audio volume keyframe added at {Fmt(local, true)}";
    }

    void ClearAudioKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_selAu == null || _selAu.VolumeKeyframes.Count == 0) return;
        if (IsObjectLocked(_selAu)) { Status.Text = "Unlock this audio track before editing it"; return; }
        Checkpoint(); _selAu.VolumeKeyframes.Clear();
        Refresh(); LoadInspector(); Status.Text = "Audio volume animation cleared";
    }

    void TextTemplate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selOv is not { IsText: true } text) return;
        var preset = TextTemplates.FirstOrDefault(p => p.Name == TextTemplateBox.SelectedItem as string);
        if (preset == null) return;
        ApplyTextTemplate(text, preset, null);
    }

    void ApplyTextTemplate(OverlayClip text, TextTemplateOption preset, string? newText)
    {
        Pause(); Checkpoint();
        if (newText != null) text.Text = newText;
        text.Color = preset.Color; text.TextShape = preset.Shape; text.FontSize = preset.FontSize;
        text.FontFamily = ComboVal(LFontFamilyBox); text.Bold = LTextBoldCheck.IsChecked == true; text.Italic = LTextItalicCheck.IsChecked == true;
        text.TextAlignment = ComboVal(LTextAlignmentBox); text.LineSpacing = LLineSpacingSlider.Value;
        text.Y = preset.Y; text.AnimationPreset = preset.Animation;
        _loading = true;
        TextTemplateBox.SelectedItem = preset.Name;
        LColorBox.SelectedItem = LColorBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Content, text.Color));
        LShapeBox.SelectedItem = LShapeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Content, text.TextShape));
        LFontSlider.Value = text.FontSize; LYSlider.Value = text.Y; AnimationBox.SelectedItem = text.AnimationPreset;
        _loading = false;
        AfterEdit();
        Status.Text = $"Applied text template · {preset.Name}";
    }

    TextTemplateOption? SelectedShelfTemplate()
    {
        string? name = WorkspaceTabs.SelectedIndex == 5 ? TemplateShelfBox.SelectedItem as string : TextTemplateShelfBox.SelectedItem as string;
        return TextTemplates.FirstOrDefault(p => p.Name == name);
    }

    void AddStyledTitle_Click(object sender, RoutedEventArgs e)
    {
        var preset = SelectedShelfTemplate();
        if (preset == null) return;
        if (_selOv is not { IsText: true }) AddTextClip();
        if (_selOv is { IsText: true } text) ApplyTextTemplate(text, preset, WorkspaceTabs.SelectedIndex == 5 ? TemplateTitleText.Text : ShelfTitleText.Text);
    }

    void ApplyTemplateShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv is not { IsText: true } text)
        {
            MessageBox.Show(this, "Select a text clip on the timeline first.", "No title selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SelectedShelfTemplate() is { } preset) ApplyTextTemplate(text, preset, null);
    }

    void ApplyEffectShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { MessageBox.Show(this, "Select a video clip on V1 first.", "No video selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Pause(); _loading = true; SelectCombo(FilterBox, EffectShelfBox.SelectedItem as string ?? "None"); _loading = false; ApplyParams(true);
        Status.Text = $"Applied effect · {ComboVal(FilterBox)}";
    }

    void ApplyFilterShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { MessageBox.Show(this, "Select a video clip on V1 first.", "No video selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Pause(); _loading = true; SelectCombo(FilterBox, FilterShelfBox.SelectedItem as string ?? "None"); _loading = false; ApplyParams(true);
        Status.Text = $"Applied filter · {ComboVal(FilterBox)}";
    }

    void ApplyTransitionShelf_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { MessageBox.Show(this, "Select a video clip on V1 first.", "No video selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Pause(); _loading = true; SelectCombo(TransitionBox, TransitionShelfBox.SelectedItem as string ?? "None"); _loading = false; ApplyParams(true);
        Status.Text = $"Applied transition · {ComboVal(TransitionBox)}";
    }

    void ZoomPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selectedZoomEvent == null) return;
        var preset = ZoomPresets.FirstOrDefault(p => p.Name == ZoomPresetBox.SelectedItem as string);
        if (preset == null) return;
        Pause();
        _loading = true;
        MouseZoomStrengthSlider.Value = preset.Strength;
        MouseZoomSpeedSlider.Value = preset.Speed;
        MouseZoomDurationSlider.Value = preset.Duration;
        SelectCombo(ZoomEasingBox, preset.Easing);
        _loading = false;
        ApplyParams(true);
        Status.Text = $"Applied zoom preset · {preset.Name}";
    }

    // ---------- keyframes ----------
    void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var o = _selOv;
        if (o == null || o.IsText) return;
        double rel = _playhead - o.Start;
        if (rel < 0 || rel > o.Duration)
        {
            MessageBox.Show("Move the playhead inside the overlay clip first (click the ruler or main track).");
            return;
        }
        Checkpoint();
        o.Keyframes.RemoveAll(k => Math.Abs(k.Time - rel) < 0.05);
        o.Keyframes.Add(new Keyframe { Time = rel, X = LXSlider.Value, Y = LYSlider.Value, Scale = LScaleSlider.Value });
        o.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        LKeyInfo.Text = $"Keyframes: {o.Keyframes.Count} (need 2 or more to animate)";
        Refresh();
    }

    void AddTextKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv is not { IsText: true } text) return;
        double rel = _playhead - text.Start;
        if (rel < 0 || rel > text.Duration)
        {
            MessageBox.Show(this, "Move the playhead inside the selected text clip before adding a keyframe.", "Text keyframe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Pause(); Checkpoint();
        var key = text.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - rel) < 0.05);
        if (key == null) { key = new Keyframe { Time = rel }; text.Keyframes.Add(key); }
        key.X = LXSlider.Value; key.Y = LYSlider.Value;
        key.Scale = Math.Clamp(LFontSlider.Value / Math.Max(1, text.FontSize), 0.1, 5);
        key.Opacity = Math.Clamp(TextOpacitySlider.Value / Math.Max(0.1, text.TextOpacity), 0, 1);
        text.Keyframes = text.Keyframes.OrderBy(k => k.Time).ToList();
        LTextKeyInfo.Text = $"Text keyframes: {text.Keyframes.Count} · move the playhead and change position, size, or opacity to animate";
        AfterEdit();
        Status.Text = $"Added text keyframe · {Fmt(rel, true)}";
    }

    void ClearTextKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv is not { IsText: true } text || text.Keyframes.Count == 0) return;
        Pause(); Checkpoint(); text.Keyframes.Clear();
        LTextKeyInfo.Text = "Text keyframes: 0";
        AfterEdit(); Status.Text = "Cleared text keyframes";
    }

    void ClearKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv == null || _selOv.Keyframes.Count == 0) return;
        Checkpoint();
        _selOv.Keyframes.Clear();
        LKeyInfo.Text = "Keyframes: 0 (need 2 or more to animate)";
        Refresh();
    }

    void SyncLayerSliders()
    {
        var o = _selOv;
        if (o == null || o.Keyframes.Count == 0 || _playhead < o.Start || _playhead > o.End) return;
        double rel = _playhead - o.Start;
        double x, y, sc, opacity;
        if (o.IsText)
        {
            (x, y, sc, opacity) = o.AtText(rel);
        }
        else
        {
            if (o.Keyframes.Count < 2) return;
            (x, y, sc) = o.At(rel); opacity = o.Opacity;
        }
        _loading = true;
        LXSlider.Value = x; LYSlider.Value = y;
        if (o.IsText)
        {
            LFontSlider.Value = Math.Clamp(o.FontSize * sc, LFontSlider.Minimum, LFontSlider.Maximum);
            TextOpacitySlider.Value = Math.Clamp(o.TextOpacity * opacity, TextOpacitySlider.Minimum, TextOpacitySlider.Maximum);
        }
        else LScaleSlider.Value = sc;
        _loading = false;
    }

    // ---------- timeline drawing ----------
    /// <summary>Tiles the clip's cached thumbnail behind the label so V1 looks like a filmstrip.</summary>
    void AddFilmstrip(Border block, string path)
    {
        try
        {
            var media = _bin.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (media?.Thumbnail is not ImageSource thumb) return;
            var strip = new Rectangle
            {
                IsHitTestVisible = false,
                Opacity = 0.62,
                Fill = new ImageBrush(thumb)
                {
                    Stretch = Stretch.UniformToFill,
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, LaneMainH * 16.0 / 9.0, LaneMainH),
                    AlignmentX = AlignmentX.Left
                }
            };
            if (strip.Fill.CanFreeze) strip.Fill.Freeze();
            if (block.Child is Grid g) g.Children.Insert(0, strip);
        }
        catch { /* thumbnails are decoration only */ }
    }

    Border Block(double left, double top, double w, double h, string label, Color col, bool sel, bool trimLeft = false, bool trimRight = false)
    {
        var content = new Grid();
        content.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11,
            Margin = new Thickness(trimLeft ? 9 : 6, 3, trimRight ? 9 : 6, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (trimLeft) content.Children.Add(new Border { Width = 4, Margin = new Thickness(1, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromArgb(190, 230, 238, 250)), CornerRadius = new CornerRadius(2) });
        if (trimRight) content.Children.Add(new Border { Width = 4, Margin = new Thickness(0, 2, 1, 2), HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromArgb(190, 230, 238, 250)), CornerRadius = new CornerRadius(2) });
        var b = new Border
        {
            Width = Math.Max(2, w - 2),
            Height = h,
            Background = new SolidColorBrush(col),
            BorderBrush = sel ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x68)) : new SolidColorBrush(Color.FromArgb(110, col.R, col.G, col.B)),
            BorderThickness = new Thickness(sel ? 2 : 0),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            IsHitTestVisible = false,
            Child = content
        };
        Canvas.SetLeft(b, left); Canvas.SetTop(b, top);
        TimelineCanvas.Children.Add(b);
        return b;
    }

    void Lane(double y, double h, string name, double width)
    {
        string track = name.Split(' ', 2)[0];
        Color accent = name.StartsWith("V1", StringComparison.Ordinal) ? Color.FromRgb(0x4B, 0xA7, 0xFF)
            : name.StartsWith("V2", StringComparison.Ordinal) ? Color.FromRgb(0x36, 0xCE, 0xDC)
            : name.StartsWith("V3", StringComparison.Ordinal) ? Color.FromRgb(0xB0, 0x83, 0xFF)
            : name.StartsWith("V4", StringComparison.Ordinal) ? Color.FromRgb(0x76, 0x9C, 0xFF)
            : name.StartsWith("T", StringComparison.Ordinal) ? Color.FromRgb(0xDB, 0x8B, 0xF4)
            : Color.FromRgb(0x56, 0xD6, 0xA2);
        var r = new Rectangle { Width = Math.Max(0, width - TrackLabelWidth), Height = h,
            Fill = new SolidColorBrush(Color.FromArgb(18, accent.R, accent.G, accent.B)), IsHitTestVisible = false };
        Canvas.SetLeft(r, TrackLabelWidth); Canvas.SetTop(r, y);
        TimelineCanvas.Children.Add(r);
        var header = new Rectangle { Width = TrackLabelWidth - 2, Height = h,
            Fill = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B)), IsHitTestVisible = false };
        Canvas.SetLeft(header, 0); Canvas.SetTop(header, y);
        TimelineCanvas.Children.Add(header);
        var t = new TextBlock { Text = name, Foreground = new SolidColorBrush(accent), FontSize = 11, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
        Canvas.SetLeft(t, 12); Canvas.SetTop(t, y + Math.Max(4, (h - 16) / 2));
        TimelineCanvas.Children.Add(t);
        AddTrackControl(track, "Mute", "M", 76, y, h);
        AddTrackControl(track, "Lock", _lockedTracks.GetValueOrDefault(track) ? "L" : "U", 98, y, h);
    }

    void AddTrackControl(string track, string action, string label, double x, double y, double h)
    {
        bool active = action == "Mute" ? _mutedTracks.GetValueOrDefault(track) : _lockedTracks.GetValueOrDefault(track);
        var button = new Button
        {
            Content = label, Width = 19, Height = Math.Min(19, h - 4), Padding = new Thickness(0), Margin = new Thickness(0),
            FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            Background = active ? (action == "Mute" ? new SolidColorBrush(Color.FromRgb(0x9B, 0x3E, 0x52)) : new SolidColorBrush(Color.FromRgb(0xA0, 0x72, 0x2E))) : new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x3B)),
            BorderThickness = new Thickness(0), Focusable = false, Tag = new TrackControlAction(track, action),
            ToolTip = action == "Mute" ? $"{(active ? "Unmute" : "Mute")} {track}" : $"{(active ? "Unlock" : "Lock")} {track}"
        };
        button.Click += TrackControl_Click;
        Canvas.SetLeft(button, x); Canvas.SetTop(button, y + Math.Max(1, (h - button.Height) / 2));
        Panel.SetZIndex(button, 20);
        TimelineCanvas.Children.Add(button);
    }

    void TrackControl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TrackControlAction action }) return;
        Checkpoint();
        var state = action.Action == "Mute" ? _mutedTracks : _lockedTracks;
        state[action.Track] = !state.GetValueOrDefault(action.Track);
        Refresh();
        Status.Text = $"{action.Track} {(state[action.Track] ? action.Action == "Mute" ? "muted" : "locked" : action.Action == "Mute" ? "unmuted" : "unlocked")}";
        e.Handled = true;
    }

    string TrackKeyFor(object item) => item switch
    {
        Clip _ => "V1", OverlayClip o when o.IsText => $"T{o.TextTrack}", OverlayClip o => $"V{o.VideoTrack}", AudioClip a => $"A{a.Track}", _ => ""
    };
    bool IsTrackMuted(string track) => _mutedTracks.GetValueOrDefault(track);
    bool IsTrackLocked(string track) => _lockedTracks.GetValueOrDefault(track);
    bool IsObjectLocked(object item) => IsTrackLocked(TrackKeyFor(item));
    bool SelectionHasLockedTrack() => _multiSelection.Count > 0
        ? _multiSelection.Any(IsObjectLocked)
        : (_selected != null && IsObjectLocked(_selected)) || (_selOv != null && IsObjectLocked(_selOv)) || (_selAu != null && IsObjectLocked(_selAu)) || (_zoomEventClip != null && IsObjectLocked(_zoomEventClip));

    void Refresh()
    {
        if (!IsLoaded) return;
        _lastGuideActiveOverlays = null;
        _zoomActionCacheClip = null;
        _zoomActionCache = null;
        TimelineCanvas.Children.Clear();
        _timelineBlocks.Clear();
        _zoomMarkerOwners.Clear();
        TimelineCanvas.Height = Math.Max(344, AudioLaneTop(_audioLaneCount) + LaneAudioH + 10);
        double pps = Pps, ext = Extent;
        double width = Math.Max(TrackLabelWidth + ext * pps + 300, 1200);
        TimelineCanvas.Width = width;

        for (int s = 0; s <= (int)ext + 15; s++)
        {
            double x = TrackLabelWidth + s * pps;
            bool major = s % 5 == 0;
            TimelineCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = major ? 8 : 16, Y2 = 24, Stroke = new SolidColorBrush(Color.FromRgb(0x45, 0x4A, 0x54)), StrokeThickness = 1, IsHitTestVisible = false });
            if (major)
            {
                var t = new TextBlock { Text = Fmt(s), Foreground = new SolidColorBrush(Color.FromRgb(0xA4, 0xAB, 0xB7)), FontSize = 10, IsHitTestVisible = false };
                Canvas.SetLeft(t, x + 3); Canvas.SetTop(t, 0);
                TimelineCanvas.Children.Add(t);
            }
        }

        Lane(LaneMainY, LaneMainH, "V1   Video", width);
        Lane(LaneOvY, LaneOvH, "V2   Overlay / Zoom", width);
        Lane(LaneOv3Y, LaneOvH, "V3   Overlay", width);
        Lane(LaneOv4Y, LaneOvH, "V4   Overlay", width);
        for (int track = 1; track <= _textLaneCount; track++)
            Lane(TextLaneY(track), LaneTextH, $"T{track}   Text", width);
        for (int track = 1; track <= _audioLaneCount; track++)
            Lane(AudioLaneTop(track), LaneAudioH, $"A{track}   Audio", width);

        double pos = 0;
        foreach (var c in _timeline)
        {
            bool sel = TimelineItemSelected(c, _selected);
            string label = c.IsGap ? $"GAP · {c.GapDuration:0.##}s" : c.Name;
            if (Math.Abs(c.Speed - 1) > 0.001) label += $"  {c.Speed:0.##}x";
            if (c.TransitionIn != "None") label += $"  [{c.TransitionIn}]";
            var mainBlock = Block(TrackLabelWidth + pos * pps, LaneMainY, c.Duration * pps, LaneMainH, label,
                  c.IsGap ? (sel ? Color.FromRgb(0x55, 0x5C, 0x70) : Color.FromRgb(0x3C, 0x42, 0x53))
                    : sel ? Color.FromRgb(0x2E, 0xA0, 0xFF) : Color.FromRgb(0x3A, 0x5A, 0x8C),
                  sel, trimLeft: !c.IsGap, trimRight: true);
            if (!c.IsGap) AddFilmstrip(mainBlock, c.Path);
            _timelineBlocks[c] = mainBlock;
            pos += c.Duration;
        }

        foreach (var o in _overlays)
        {
            bool sel = TimelineItemSelected(o, _selOv);
            if (o.IsText)
                _timelineBlocks[o] = Block(TrackLabelWidth + o.Start * pps, TextLaneY(o.TextTrack) + 2, o.Duration * pps, LaneTextH - 4, o.Name,
                    o.TextTrack switch { 2 => Color.FromRgb(0x9B, 0x63, 0xC8), 3 => Color.FromRgb(0xBD, 0x77, 0xD8), _ => Color.FromRgb(0x7D, 0x55, 0x9F) }, sel, trimLeft: true, trimRight: true);
            else
                _timelineBlocks[o] = Block(TrackLabelWidth + o.Start * pps, VideoLaneY(o.VideoTrack) + 2, o.Duration * pps, LaneOvH - 4,
                      o.Name + (o.Keyframes.Count >= 2 ? "  (anim)" : ""), o.VideoTrack switch { 3 => Color.FromRgb(0x66, 0x4F, 0xA0), 4 => Color.FromRgb(0x48, 0x67, 0xA0), _ => Color.FromRgb(0x7A, 0x4F, 0xA0) }, sel, trimLeft: true, trimRight: true);
        }

        double videoStart = 0;
        var zoomRowEnds = Enumerable.Repeat(double.NegativeInfinity, 2).ToArray();
        foreach (var clip in _timeline)
        {
            foreach (var zoom in clip.PointerEvents.OrderBy(z => z.TimeSeconds))
            {
                if ((!zoom.IsClick && !zoom.IsTyping) || zoom.TimeSeconds < clip.In || zoom.TimeSeconds > clip.Out) continue;
                double time = videoStart + (zoom.TimeSeconds - clip.In) / clip.Speed;
                double markerWidth = Math.Max(14, zoom.ZoomDurationSeconds * pps);
                double left = Math.Max(TrackLabelWidth, TrackLabelWidth + time * pps);
                int row = Array.FindIndex(zoomRowEnds, end => left >= end + 2);
                if (row < 0) row = Array.IndexOf(zoomRowEnds, zoomRowEnds.Min());
                zoomRowEnds[row] = left + markerWidth;
                var marker = Block(left, LaneOvY + 2 + row * 15, markerWidth, 13, "ZOOM", Color.FromRgb(0x00, 0xAE, 0xD7), zoom == _selectedZoomEvent);
                marker.CornerRadius = new CornerRadius(7);
                var markerContent = new Grid();
                markerContent.Children.Add(new TextBlock { Text = markerWidth >= 42 ? "ZOOM" : zoom.IsTyping ? "T" : "Z",
                    ToolTip = $"{(zoom.IsTyping ? "Typing focus zoom" : "Click zoom")} · starts {Fmt(time, true)} · {zoom.ZoomDurationSeconds:0.00}s · {zoom.ZoomStrength:0.00}× · drag the body to retime or the right edge to resize",
                    Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                markerContent.Children.Add(new Border { Width = 3, Margin = new Thickness(0, 3, 1, 3), CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Drag to change zoom duration" });
                marker.Child = markerContent;
                _timelineBlocks[zoom] = marker;
                _zoomMarkerOwners[zoom] = clip;
            }
            videoStart += clip.Duration;
        }
        foreach (var a in _audios)
        {
            var block = Block(TrackLabelWidth + a.Start * pps, AudioLaneTop(a.Track) + 1, a.Duration * pps, LaneAudioH - 2,
                a.Name, Color.FromRgb(0x1D, 0x72, 0x54), TimelineItemSelected(a, _selAu), trimLeft: true, trimRight: true);
            if (!string.IsNullOrWhiteSpace(a.WaveformPath) && File.Exists(a.WaveformPath))
            {
                try
                {
                    if (!_waveformCache.TryGetValue(a.WaveformPath, out var waveform))
                    {
                        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 512; image.UriSource = new Uri(a.WaveformPath); image.EndInit(); image.Freeze();
                        waveform = image; _waveformCache[a.WaveformPath] = waveform;
                    }
                    block.Background = new ImageBrush(waveform) { Stretch = Stretch.Fill, Opacity = 0.84 };
                }
                catch { a.WaveformPath = ""; }
            }
        }

        _playLine = new Line { Y1 = 0, Y2 = 338, Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0x55, 0x65)), StrokeThickness = 2, IsHitTestVisible = false };
        TimelineCanvas.Children.Add(_playLine);
        UpdatePlayhead();
        UpdateGuide();
        UpdatePreviewMouseZoom();
    }

    void UpdatePlayhead()
    {
        if (_playLine != null) { _playLine.X1 = _playLine.X2 = TrackLabelWidth + _playhead * Pps; }
        TimeText.Text = $"{FrameTimecode(_playhead)} / {FrameTimecode(Extent)}";
        TimeText.ToolTip = $"Frame-accurate timecode · {TimelineFps} fps · frame {Math.Round(_playhead * TimelineFps):N0}";
        _updatingPreviewSeek = true;
        PreviewSeekSlider.Maximum = Math.Max(0.01, Extent);
        PreviewSeekSlider.Value = Math.Clamp(_playhead, 0, PreviewSeekSlider.Maximum);
        PreviewClock.Text = $"{Fmt(_playhead)} / {Fmt(Extent)}";
        _updatingPreviewSeek = false;
    }

    void OnPlayheadMoved()
    {
        UpdatePlayhead();
        SyncLayerSliders();
        UpdateGuide();
        SyncAudioPreview();
        UpdatePreviewMouseZoom();
    }

    void UpdatePreviewMouseZoom()
    {
        if (PreviewVideoLayer == null) return;
        var (clip, sourceTime, _) = Locate(_playhead);
        UpdatePreviewClickEffect(clip, sourceTime);
        if (clip != null && ApplyKeyframePreview(clip, sourceTime)) return;
        if (clip == null || !clip.AutoMouseZoom || clip.PointerEvents.Count == 0)
        {
            PreviewZoomScale.ScaleX = PreviewZoomScale.ScaleY = 1;
            PreviewZoomTranslate.X = PreviewZoomTranslate.Y = 0;
            return;
        }

        double t = Math.Max(0, (sourceTime - clip.In) / clip.Speed);
        double mouseX = 0.5, mouseY = 0.5, pulseMax = 0;
        if (!ReferenceEquals(_zoomActionCacheClip, clip) || _zoomActionCache == null)
        {
            _zoomActionCacheClip = clip;
            _zoomActionCache = clip.PointerEvents
                .Where(action => action.TimeSeconds >= clip.In && action.TimeSeconds <= clip.Out)
                .Select(action => (Action: action, Time: (action.TimeSeconds - clip.In) / clip.Speed))
                .OrderBy(entry => entry.Time).ToList();
        }
        var actions = _zoomActionCache;
        foreach (var entry in actions)
        {
            var action = entry.Action;
            if (!action.IsClick && !action.IsTyping) continue;
            double dt = t - entry.Time;
            double duration = Math.Clamp(action.ZoomDurationSeconds, 0.2, 4);
            double ramp = Math.Clamp(action.ZoomSpeedSeconds, 0.05, duration / 2);
            if (dt < 0 || dt >= duration) continue;
            double riseU = Math.Clamp(dt / ramp, 0, 1);
            double rise = EaseZoom(riseU, action.ZoomEasing);
            double releaseU = Math.Clamp((dt - (duration - ramp)) / ramp, 0, 1);
            double release = 1 - EaseZoom(releaseU, action.ZoomEasing);
            double amount = Math.Max(0, Math.Clamp(action.ZoomStrength, 1, 2) - 1);
            pulseMax = Math.Max(pulseMax, amount * rise * release);
        }

        var previous = actions.LastOrDefault(entry => entry.Time <= t);
        var next = actions.FirstOrDefault(entry => entry.Time > t);
        if (previous.Action != null)
        {
            mouseX = previous.Action.X;
            mouseY = previous.Action.Y;
            if (next.Action != null)
            {
                double span = Math.Max(0.08, next.Time - previous.Time);
                double u = Math.Clamp((t - previous.Time) / span, 0, 1);
                u = u * u * (3 - 2 * u);
                mouseX += (next.Action.X - mouseX) * u;
                mouseY += (next.Action.Y - mouseY) * u;
            }
        }
        else if (next.Action != null)
        {
            mouseX = next.Action.X;
            mouseY = next.Action.Y;
        }

            double zoom = Math.Clamp(1 + pulseMax, 1, 2);
        PreviewZoomScale.ScaleX = PreviewZoomScale.ScaleY = zoom;
        double aspect = 16.0 / 9.0;
        if (clip.IsImage && PreviewImage.Source is BitmapSource bitmap && bitmap.PixelHeight > 0)
            aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        else if (Preview.NaturalVideoWidth > 0 && Preview.NaturalVideoHeight > 0)
            aspect = (double)Preview.NaturalVideoWidth / Preview.NaturalVideoHeight;
        double fw = PreviewHost.ActualWidth, fh = PreviewHost.ActualHeight;
        if (fw <= 0 || fh <= 0) return;
        // Match FFmpeg's clamped crop: keep the zoomed frame inside the preview
        // instead of translating past its edges and exposing black gaps.
        double focusX = (clip.FrameX + (mouseX - 0.5) * clip.FrameScale) * fw - fw / 2;
        double focusY = (clip.FrameY + (mouseY - 0.5) * clip.FrameScale) * fh - fh / 2;
        PreviewZoomTranslate.X = Math.Clamp(-zoom * focusX, -(zoom - 1) * fw / 2, (zoom - 1) * fw / 2);
        PreviewZoomTranslate.Y = Math.Clamp(-zoom * focusY, -(zoom - 1) * fh / 2, (zoom - 1) * fh / 2);
    }

    void UpdatePreviewClickEffect(Clip? clip, double sourceTime)
    {
        if (PreviewClickCanvas == null) return;
        PreviewClickCanvas.Children.Clear();
        if (clip == null || !clip.ShowClickEffects || PreviewHost.ActualWidth < 10 || PreviewHost.ActualHeight < 10) return;
        double local = (sourceTime - clip.In) / clip.Speed;
        var click = clip.PointerEvents.Where(p => p.IsClick)
            .Select(p => (Event: p, At: (p.TimeSeconds - clip.In) / clip.Speed))
            .Where(p => local >= p.At && local < p.At + Math.Clamp(clip.ClickEffectDuration, 0.15, 1.5))
            .OrderByDescending(p => p.At).FirstOrDefault();
        if (click.Event == null) return;

        double progress = Math.Clamp((local - click.At) / Math.Clamp(clip.ClickEffectDuration, 0.15, 1.5), 0, 1);
        double aspect = 16.0 / 9.0;
        if (clip.IsImage && PreviewImage.Source is BitmapSource bitmap && bitmap.PixelHeight > 0)
            aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        else if (Preview.NaturalVideoWidth > 0 && Preview.NaturalVideoHeight > 0)
            aspect = (double)Preview.NaturalVideoWidth / Preview.NaturalVideoHeight;
        double w = PreviewHost.ActualWidth, h = PreviewHost.ActualHeight;
        double frameW = w, frameH = w / aspect;
        if (frameH > h) { frameH = h; frameW = h * aspect; }
        double left = (clip.FrameX + (Math.Clamp(click.Event.X, 0, 1) - 0.5) * clip.FrameScale) * w;
        double top = (clip.FrameY + (Math.Clamp(click.Event.Y, 0, 1) - 0.5) * clip.FrameScale) * h;
        double eased = 1 - Math.Pow(1 - progress, 3);
        double startScale = clip.ClickEffectStyle switch { "Bold ripple" => 0.62, "Halo ring" => 0.78, _ => 0.68 };
        double endScale = clip.ClickEffectStyle switch { "Bold ripple" => 1.55, "Halo ring" => 1.28, _ => 1.2 };
        double diameter = Math.Clamp(clip.ClickEffectSize, 0.03, 0.2) * Math.Min(frameW, frameH) * clip.FrameScale * (startScale + eased * (endScale - startScale));
        Brush ring;
        try { ring = (Brush)new BrushConverter().ConvertFromString(clip.ClickEffectColor)!; }
        catch { ring = new SolidColorBrush(Color.FromRgb(0x61, 0xD9, 0xFF)); }
        double strength = clip.ClickEffectStyle switch { "Bold ripple" => 0.82, "Halo ring" => 0.56, _ => 0.34 };
        var pulse = new Ellipse
        {
            Width = diameter, Height = diameter, Fill = Brushes.Transparent,
            Stroke = ring, StrokeThickness = Math.Max(2, frameW / (clip.ClickEffectStyle == "Bold ripple" ? 260 : 420)),
            Opacity = strength * Math.Pow(1 - progress, clip.ClickEffectStyle == "Soft click pulse" ? 1.8 : 1.2), IsHitTestVisible = false
        };
        Canvas.SetLeft(pulse, left - diameter / 2); Canvas.SetTop(pulse, top - diameter / 2);
        PreviewClickCanvas.Children.Add(pulse);
        if (clip.ClickEffectStyle == "Bold ripple")
        {
            double inner = diameter * 0.58;
            var innerRing = new Ellipse { Width = inner, Height = inner, Fill = Brushes.Transparent, Stroke = ring, StrokeThickness = Math.Max(2, frameW / 500), Opacity = strength * (1 - progress), IsHitTestVisible = false };
            Canvas.SetLeft(innerRing, left - inner / 2); Canvas.SetTop(innerRing, top - inner / 2);
            PreviewClickCanvas.Children.Add(innerRing);
        }
    }
    // ---------- preview guide (shows where overlays/text will appear) ----------
    static Brush TextBrush(string name) => name switch
    {
        "Yellow" => Brushes.Yellow,
        "Red" => Brushes.Red,
        "Black" => Brushes.Black,
        "Cyan" => Brushes.Cyan,
        "Gold" => Brushes.Gold,
        "Pink" => Brushes.HotPink,
        "Violet" => Brushes.Violet,
        "Orange" => Brushes.Orange,
        "Lime" => Brushes.Lime,
        _ => Brushes.White
    };
    static Color ToMediaColor(string name) => (TextBrush(name) as SolidColorBrush)?.Color ?? Colors.White;

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGuide(); UpdatePreviewMouseZoom();
        if (Locate(_playhead).clip is { } clip) UpdateMainFrameTransform(clip);
    }

    void UpdateMainFrameTransform(Clip? clip)
    {
        if (PreviewFrameScaleTransform == null || PreviewFrameTranslateTransform == null) return;
        double scale = clip == null ? 1 : Math.Clamp(clip.FrameScale, 0.2, 2.0);
        double x = clip == null ? 0.5 : Math.Clamp(clip.FrameX, 0, 1);
        double y = clip == null ? 0.5 : Math.Clamp(clip.FrameY, 0, 1);
        PreviewFrameScaleTransform.ScaleX = PreviewFrameScaleTransform.ScaleY = scale;
        PreviewFrameTranslateTransform.X = (x - 0.5) * PreviewHost.ActualWidth;
        PreviewFrameTranslateTransform.Y = (y - 0.5) * PreviewHost.ActualHeight;
    }

    void ResetMainFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || IsObjectLocked(_selected)) return;
        Checkpoint();
        _selected.FrameScale = 1; _selected.FrameX = 0.5; _selected.FrameY = 0.5;
        _loading = true;
        MainFrameScaleSlider.Value = 1; MainFrameXSlider.Value = MainFrameYSlider.Value = 0.5;
        _loading = false;
        AfterEdit();
        Status.Text = "Video frame reset · centered at full size";
    }

    void UpdateGuide()
    {
        if (GuideCanvas == null || PreviewHost == null) return;
        var activeOverlays = _overlays.Where(o => _playhead >= o.Start && _playhead <= o.End && !IsTrackMuted(TrackKeyFor(o)))
            .OrderBy(o => o.IsText ? 5 : o.VideoTrack).ToArray();
        bool hasAnimatedOverlay = activeOverlays.Any(o => o.AnimationPreset != "None" || o.Keyframes.Count >= 2);
        if (_playing && !hasAnimatedOverlay && _lastGuideActiveOverlays != null &&
            activeOverlays.SequenceEqual(_lastGuideActiveOverlays))
        {
            foreach (var overlay in activeOverlays.Where(o => !o.IsText && !o.IsImage))
                if (_overlayVideoPlayers.TryGetValue(overlay, out var player)) SyncOverlayVideoPlayer(overlay, player);
            CleanupOverlayVideoPlayers();
            return;
        }
        _lastGuideActiveOverlays = activeOverlays;
        GuideCanvas.Children.Clear();
        OverlayImageCanvas2.Children.Clear();
        OverlayImageCanvas3.Children.Clear();
        OverlayImageCanvas4.Children.Clear();
        double bw = PreviewHost.ActualWidth, bh = PreviewHost.ActualHeight;
        if (bw < 20 || bh < 20) return;
        double fw = bw, fh = bw * 9 / 16;
        if (fh > bh) { fh = bh; fw = bh * 16 / 9; }
        double ox = (bw - fw) / 2, oy = (bh - fh) / 2;

        foreach (var o in activeOverlays)
        {
            bool sel = o == _selOv;
            double x, y, sc, keyOpacity;
            if (o.IsText)
            {
                if (sel) { x = LXSlider.Value; y = LYSlider.Value; sc = LFontSlider.Value / Math.Max(1, o.FontSize); keyOpacity = TextOpacitySlider.Value / Math.Max(0.1, o.TextOpacity); }
                else (x, y, sc, keyOpacity) = o.AtText(_playhead - o.Start);
            }
            else
            {
                if (sel) { x = LXSlider.Value; y = LYSlider.Value; sc = LScaleSlider.Value; }
                else (x, y, sc) = o.At(_playhead - o.Start);
                keyOpacity = 1;
            }

            FrameworkElement el;
            if (o.IsText)
            {
                var tb = new TextBlock
                {
                    Text = o.Text, MaxWidth = fw * 0.9, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = o.TextAlignment switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center },
                    FontFamily = new FontFamily(o.FontFamily),
                    FontSize = Math.Max(6, (sel ? LFontSlider.Value : o.FontSize * sc) / 1080.0 * fh),
                    Foreground = TextBrush(o.Color),
                    FontWeight = o.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = o.Italic ? FontStyles.Italic : FontStyles.Normal,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    LineHeight = Math.Max(6, Math.Max(6, (sel ? LFontSlider.Value : o.FontSize * sc) / 1080.0 * fh) + o.LineSpacing * fh / 1080.0)
                };
                var textSurface = new Grid { MaxWidth = fw * 0.9 };
                double outline = Math.Clamp(o.TextOutlineWidth * fh / 1080.0, 0, 8);
                if (outline > 0)
                {
                    foreach (var (dx, dy) in new[] { (-1d, -1d), (0d, -1d), (1d, -1d), (-1d, 0d), (1d, 0d), (-1d, 1d), (0d, 1d), (1d, 1d) })
                    {
                        var edge = new TextBlock
                        {
                            Text = tb.Text, MaxWidth = tb.MaxWidth, TextWrapping = tb.TextWrapping, TextAlignment = tb.TextAlignment,
                            FontFamily = tb.FontFamily, FontSize = tb.FontSize, FontWeight = tb.FontWeight, FontStyle = tb.FontStyle,
                            LineStackingStrategy = tb.LineStackingStrategy, LineHeight = tb.LineHeight,
                            Foreground = TextBrush(o.TextOutlineColor), IsHitTestVisible = false,
                            RenderTransform = new TranslateTransform(dx * outline, dy * outline)
                        };
                        textSurface.Children.Add(edge);
                    }
                }
                textSurface.Children.Add(tb);
                if (o.TextShadow)
                    textSurface.Effect = new DropShadowEffect { Color = ToMediaColor(o.TextShadowColor), BlurRadius = Math.Clamp(o.TextShadowSoftness * fh / 1080.0, 0, 24), ShadowDepth = 2, Opacity = 0.75 };
                el = new Border
                {
                    BorderBrush = sel ? Brushes.Cyan : Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Background = !sel && o.TextShape == "Caption box" ? new SolidColorBrush(Color.FromArgb(190, 12, 14, 18)) :
                                 !sel && o.TextShape == "Highlight box" ? new SolidColorBrush(Color.FromArgb(210, 20, 120, 150)) : Brushes.Transparent,
                    CornerRadius = new CornerRadius(o.TextShape == "None" ? 0 : 7),
                    Padding = new Thickness(o.TextShape == "None" ? outline + 1 : 8),
                    Child = textSurface
                };
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double alignOffset = o.TextAlignment switch { "Left" => 0, "Right" => el.DesiredSize.Width, _ => el.DesiredSize.Width / 2 };
                Canvas.SetLeft(el, ox + x * fw - alignOffset);
                Canvas.SetTop(el, oy + y * fh - el.DesiredSize.Height / 2);
            }
            else
            {
                double w = sc * fw, h = w / Math.Max(0.1, o.AspectRatio);
                if (o.IsImage)
                {
                    if (!_overlayImageCache.TryGetValue(o.Path, out var bitmap))
                    {
                        try { bitmap = LoadImage(o.Path); _overlayImageCache[o.Path] = bitmap; }
                        catch { bitmap = null!; }
                    }
                    el = bitmap == null
                        ? new Border { Width = w, Height = h, Background = Brushes.DarkRed, Child = new TextBlock { Text = "Image unavailable", Foreground = Brushes.White, Margin = new Thickness(5) } }
                        : new Image { Source = bitmap, Width = w, Height = h, Stretch = Stretch.Uniform };
                    if (sel)
                    {
                        el = new Border { Width = w, Height = h, BorderBrush = Brushes.Cyan, BorderThickness = new Thickness(2), Child = el };
                    }
                }
                else
                {
                    UpdateOverlayVideoPreview(o, x, y, w, h, fw, fh, ox, oy, sel);
                    continue;
                }
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                Canvas.SetLeft(el, ox + x * fw - w / 2);
                Canvas.SetTop(el, oy + y * fh - h / 2);
            }
            var visual = AnimationVisual(o, _playhead - o.Start);
            el.Opacity = visual.opacity * (o.IsText ? Math.Clamp((sel ? TextOpacitySlider.Value / Math.Max(0.1, o.TextOpacity) : keyOpacity) * o.TextOpacity, 0, 1) : 1);
            el.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(visual.scale, visual.scale),
                    new RotateTransform(visual.rotation),
                    new TranslateTransform(visual.dx * fw, visual.dy * fh)
                }
            };
            if (o.IsText) GuideCanvas.Children.Add(el);
            else ImageCanvasForTrack(o.VideoTrack).Children.Add(el);
        }
        CleanupOverlayVideoPlayers();
    }

    void UpdateOverlayVideoPreview(OverlayClip overlay, double x, double y, double width, double height,
        double frameWidth, double frameHeight, double offsetX, double offsetY, bool selected)
    {
        if (!File.Exists(overlay.Path)) return;
        if (!_overlayVideoPlayers.TryGetValue(overlay, out var player))
        {
            player = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
                Volume = 0,
                IsMuted = true,
                Visibility = Visibility.Collapsed
            };
            player.MediaOpened += (_, _) => SyncOverlayVideoPlayer(overlay, player);
            _overlayVideoPlayers[overlay] = player;
        }
        string previewPath = PreviewMediaPath(overlay.Path);
        if (player.Source?.LocalPath != previewPath) player.Source = new Uri(previewPath);
        var videoCanvas = VideoCanvasForTrack(overlay.VideoTrack);
        if (player.Parent is Canvas previousCanvas && previousCanvas != videoCanvas)
            previousCanvas.Children.Remove(player);
        if (!videoCanvas.Children.Contains(player)) videoCanvas.Children.Add(player);
        player.Width = width; player.Height = height;
        Canvas.SetLeft(player, offsetX + x * frameWidth - width / 2);
        Canvas.SetTop(player, offsetY + y * frameHeight - height / 2);
        player.Opacity = selected ? 0.88 : overlay.Opacity;
        player.RenderTransformOrigin = new Point(0.5, 0.5);
        var animation = AnimationVisual(overlay, _playhead - overlay.Start);
        player.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection
            {
                new ScaleTransform(animation.scale, animation.scale),
                new RotateTransform(animation.rotation),
                new TranslateTransform(animation.dx * frameWidth, animation.dy * frameHeight)
            }
        };
        player.Visibility = Visibility.Visible;
        SyncOverlayVideoPlayer(overlay, player);
    }

    void SyncOverlayVideoPlayer(OverlayClip overlay, MediaElement player)
    {
        if (player.Source == null || player.NaturalDuration == Duration.Automatic) return;
        var relative = Math.Clamp(_playhead - overlay.Start, 0, overlay.Duration);
        var sourcePosition = TimeSpan.FromSeconds(Math.Max(0, overlay.In + relative));
        if (Math.Abs((player.Position - sourcePosition).TotalSeconds) > 0.25)
            player.Position = sourcePosition;
        if (_playing) player.Play(); else player.Pause();
    }

    void CleanupOverlayVideoPlayers()
    {
        foreach (var overlay in _overlayVideoPlayers.Keys.Where(o => !_overlays.Contains(o)).ToList())
        {
            var player = _overlayVideoPlayers[overlay];
            player.Stop();
            if (player.Parent is Canvas canvas) canvas.Children.Remove(player);
            _overlayVideoPlayers.Remove(overlay);
        }
        foreach (var pair in _overlayVideoPlayers)
        {
            if (_overlays.Contains(pair.Key) && !IsTrackMuted(TrackKeyFor(pair.Key)) && _playhead >= pair.Key.Start && _playhead <= pair.Key.End) continue;
            pair.Value.Pause();
            pair.Value.Visibility = Visibility.Collapsed;
        }
    }

    static (double scale, double rotation, double opacity, double dx, double dy) AnimationVisual(OverlayClip o, double relativeTime)
    {
        var option = AnimationPresets.FirstOrDefault(p => p.Name == o.AnimationPreset);
        if (option == null) return (1, 0, 1, 0, 0);
        double span = Math.Clamp(o.Duration * (0.14 + option.Variation * 0.035), 0.18, 1.2);
        double p = Math.Clamp(relativeTime / span, 0, 1);
        double smooth = p * p * (3 - 2 * p);
        double scale = 1, rotation = 0, opacity = 1, dx = 0, dy = 0;
        switch (option.Family)
        {
            case "Zoom In": scale = 0.12 + 0.88 * smooth; break;
            case "Zoom Out": scale = 1.45 - 0.45 * smooth; break;
            case "Spin In": scale = 0.2 + 0.8 * smooth; rotation = 360 * (1 - smooth); break;
            case "Spin Out": scale = 1.4 - 0.4 * smooth; rotation = -360 * (1 - smooth); break;
            case "Bubble Pop": scale = 0.5 + 0.5 * smooth + 0.32 * Math.Sin(Math.PI * p) * (1 - p); break;
            case "Bounce": scale = 0.7 + 0.3 * smooth + 0.22 * Math.Abs(Math.Sin(Math.PI * 3 * p)) * (1 - p); break;
            case "Fade In": opacity = smooth; break;
            case "Slide Left": dx = -(1 - smooth) * 0.35; break;
            case "Slide Right": dx = (1 - smooth) * 0.35; break;
            case "Slide Up": dy = -(1 - smooth) * 0.35; break;
            case "Slide Down": dy = (1 - smooth) * 0.35; break;
            case "Pulse": scale = 1 + 0.08 * Math.Sin(Math.PI * 4 * p) * (1 - p); break;
            case "Shake": dx = Math.Sin(p * Math.PI * 12) * 0.025 * (1 - p); break;
            case "Flip": scale = Math.Max(0.08, Math.Abs(Math.Cos(Math.PI * (1 - smooth)))); break;
            case "Elastic": scale = 1 - Math.Exp(-5 * p) * Math.Cos(10 * Math.PI * p) * (1 - p); break;
            case "Wipe": opacity = smooth; dx = -(1 - smooth) * 0.12; break;
            case "Drift": dy = -(1 - smooth) * 0.12; opacity = 0.3 + 0.7 * smooth; break;
            case "Rise": dy = (1 - smooth) * 0.28; break;
            case "Drop": dy = -(1 - smooth) * 0.28; break;
            case "Grow": scale = 0.18 + 0.82 * smooth; break;
            case "Shrink": scale = 1.32 - 0.32 * smooth; break;
            case "Twist": scale = 0.35 + 0.65 * smooth; rotation = 540 * (1 - smooth); break;
            case "Float": dy = Math.Sin(Math.PI * 2 * p) * 0.06 * (1 - p); break;
            case "Pop": scale = 0.2 + 0.8 * smooth + 0.4 * Math.Sin(Math.PI * p) * (1 - p); break;
        }
        return (scale, rotation, opacity, dx, dy);
    }

    // ---------- preview / playback ----------
    static BitmapImage LoadImage(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    int SelectedProxyHeight => PreviewQualityBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int height) ? height : 0;

    string PreviewMediaPath(string source) => _useLowMemoryProxies && SelectedProxyHeight > 0
        ? FFmpegService.FindProxy(source, SelectedProxyHeight) ?? source
        : source;

    async void PreviewQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading || PreviewQualityBox == null) return;
        int height = SelectedProxyHeight;
        if (height == 0)
        {
            _useLowMemoryProxies = false;
            _loading = true; ProxyModeCheck.IsChecked = false; _loading = false;
            LowMemoryMenuItem.IsChecked = false;
            _projectDirty = true;
            ShowFrame(false); Refresh();
            Status.Text = "Original-quality preview enabled";
            return;
        }

        var sources = ProxySources();
        if (sources.Count == 0)
        {
            Status.Text = "Import a video to use proxy playback";
            return;
        }
        if (!FFmpegService.ToolsInstalled && !await EnsureMediaToolsInteractive()) return;
        Pause();
        ExportBar.Visibility = Visibility.Visible; ExportBar.IsIndeterminate = false; ExportBar.Value = 0;
        try
        {
            int missing = sources.Count(s => FFmpegService.FindProxy(s, height) == null);
            int done = 0;
            foreach (string source in sources)
            {
                if (FFmpegService.FindProxy(source, height) == null)
                    await FFmpegService.CreateProxyAsync(source, height);
                done++;
                ExportBar.Value = done * 100.0 / sources.Count;
                Status.Text = $"Preparing {height}p preview · {done}/{sources.Count}";
            }
            _useLowMemoryProxies = true;
            _loading = true; ProxyModeCheck.IsChecked = true; LowMemoryMenuItem.IsChecked = true; _loading = false;
            _projectDirty = true;
            ShowFrame(false); Refresh();
            Status.Text = $"{height}p proxy preview ready · {sources.Count} video(s)";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not prepare proxy preview";
            MessageBox.Show(this, ex.Message, "Proxy setup failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _loading = false; ExportBar.Visibility = Visibility.Collapsed; }
    }

    List<string> ProxySources() => _bin.Where(m => m.Kind == MediaKind.Video).Select(m => m.Path)
        .Concat(_timeline.Where(c => !c.IsImage && !c.IsGap).Select(c => c.Path))
        .Concat(_overlays.Where(o => !o.IsText && !o.IsImage).Select(o => o.Path))
        .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    void ProxyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ProxyModeCheck == null) return;
        _useLowMemoryProxies = ProxyModeCheck.IsChecked == true;
        if (_useLowMemoryProxies && SelectedProxyHeight == 0 && PreviewQualityBox != null)
            PreviewQualityBox.SelectedIndex = 2;
        if (LowMemoryMenuItem != null) LowMemoryMenuItem.IsChecked = _useLowMemoryProxies;
        if (!IsLoaded) return;
        if (!_loading) _projectDirty = true;
        ShowFrame(false);
        Refresh();
        Status.Text = _useLowMemoryProxies
            ? "Low-memory preview enabled · build proxies for smaller preview files"
            : "Original media preview enabled";
    }

    async void BuildProxies_Click(object sender, RoutedEventArgs e)
    {
        var sources = ProxySources();
        if (sources.Count == 0)
        {
            MessageBox.Show(this, "Import or add at least one video before building preview proxies.", "No videos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!FFmpegService.ToolsInstalled && !await EnsureMediaToolsInteractive()) return;
        await LoadRecordingAudioDevicesAsync();

        Pause();
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        ExportBar.Visibility = Visibility.Visible;
        ExportBar.IsIndeterminate = false;
        ExportBar.Value = 0;
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                Status.Text = $"Building low-memory preview {i + 1}/{sources.Count}…";
                await FFmpegService.CreateProxyAsync(sources[i], SelectedProxyHeight is 0 ? 540 : SelectedProxyHeight);
                ExportBar.Value = (i + 1) * 100.0 / sources.Count;
            }
            _loading = true;
            PreviewQualityBox.SelectedIndex = 2;
            ProxyModeCheck.IsChecked = true;
            _loading = false;
            _useLowMemoryProxies = true;
            _projectDirty = true;
            ShowFrame(false);
            Refresh();
            Status.Text = $"Low-memory preview ready · {sources.Count} proxy file(s)";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not build preview proxies";
            MessageBox.Show(this, ex.Message, "Proxy setup failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _loading = false;
            ExportBar.Visibility = Visibility.Collapsed;
            if (button != null) button.IsEnabled = true;
        }
    }

    void ShowFrame(bool play)
    {
        ColorGradePreviewImage.Visibility = Visibility.Collapsed;
        if (play) { _livePreviewCts?.Cancel(); _livePreviewCts = null; }
        var (clip, src, _) = Locate(_playhead);
        if (clip == null)
        {
            if (_timeline.Count > 0 && _playhead >= Total - 0.001 && _playhead < Extent - 0.001)
            {
                ShowLastVideoFrame();
                return;
            }
            UpdateMainFrameTransform(null);
            _previewClip = null;
            Preview.Source = null; PreviewImage.Visibility = Visibility.Collapsed; _loadedPath = null;
            WelcomePanel.Visibility = Visibility.Visible;
            return;
        }
        if (clip.IsGap)
        {
            UpdateMainFrameTransform(null);
            _previewClip = null;
            Preview.Stop(); Preview.Source = null; PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed; ColorGradePreviewImage.Visibility = Visibility.Collapsed;
            PreviewVideoLayer.Background = Brushes.Black;
            _loadedPath = GapPreviewToken;
            WelcomePanel.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateMainFrameTransform(clip);
        PreviewVideoLayer.Background = Brushes.Transparent;
        if (!File.Exists(clip.Path))
        {
            _previewClip = null;
            Preview.Source = null; PreviewImage.Source = null; PreviewImage.Visibility = Visibility.Collapsed; _loadedPath = null;
            WelcomePanel.Visibility = Visibility.Visible;
            Status.Text = "A source file is missing. Relink the media and reopen the project.";
            return;
        }
        WelcomePanel.Visibility = Visibility.Collapsed;

        if (clip.IsImage)
        {
            _previewClip = clip;
            if (_loadedPath != clip.Path)
            {
                _loadedPath = clip.Path;
                Preview.Source = null;
                try { PreviewImage.Source = LoadImage(clip.Path); } catch { }
            }
            PreviewImage.Visibility = Visibility.Visible;
            return;
        }
        _previewClip = clip;
        PreviewImage.Visibility = Visibility.Collapsed;

        string previewPath = PreviewMediaPath(clip.Path);
        Preview.SpeedRatio = clip.Speed;
        Preview.Volume = IsTrackMuted("V1") ? 0 : Math.Min(1, clip.Volume);

        if (_loadedPath != previewPath)
        {
            _loadedPath = previewPath;
            _pendingSeek = src;
            Preview.Source = new Uri(previewPath);
            Preview.Play();            // starts opening; MediaOpened applies the seek
            return;
        }
        Preview.Position = TimeSpan.FromSeconds(src);
        if (play) Preview.Play(); else { Preview.Play(); Preview.Pause(); }
        if (!play) ScheduleLiveGradePreview(clip, src);
    }

    void ScheduleLiveGradePreview(Clip clip, double sourceTime)
    {
        if (_playing || clip.IsImage || !File.Exists(clip.Path))
        {
            ColorGradePreviewImage.Visibility = Visibility.Collapsed;
            return;
        }
        _livePreviewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _livePreviewCts = cts;
        int generation = ++_livePreviewGeneration;
        var snapshot = clip.Clone();
        string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"myeditor-grade-preview-{Environment.ProcessId}-{generation}.jpg");
        _ = RenderLiveGradePreviewAsync(snapshot, sourceTime, output, generation, cts);
    }

    async Task RenderLiveGradePreviewAsync(Clip clip, double sourceTime, string output, int generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(180, cts.Token);
            int width = Math.Clamp((int)(PreviewHost.ActualWidth * 1.25), 480, 720);
            int height = Math.Clamp((int)(PreviewHost.ActualHeight * 1.25), 270, 480);
            width -= width % 2; height -= height % 2;
            await FFmpegService.RenderColorPreviewFrameAsync(clip, sourceTime, output, width, height, cts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _livePreviewGeneration || _playing || !File.Exists(output)) return;
                try
                {
                    ColorGradePreviewImage.Source = LoadImage(output);
                    ColorGradePreviewImage.Visibility = Visibility.Visible;
                }
                catch { ColorGradePreviewImage.Visibility = Visibility.Collapsed; }
            });
        }
        catch (OperationCanceledException) { }
        catch { await Dispatcher.InvokeAsync(() => { if (generation == _livePreviewGeneration) ColorGradePreviewImage.Visibility = Visibility.Collapsed; }); }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            if (ReferenceEquals(_livePreviewCts, cts)) _livePreviewCts = null;
            cts.Dispose();
        }
    }

    void Preview_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_pendingSeek >= 0) { Preview.Position = TimeSpan.FromSeconds(_pendingSeek); _pendingSeek = -1; }
        if (!_playing || _playhead >= Total - 0.001) Preview.Pause();
        UpdatePreviewMouseZoom();
        var (clip, sourceTime, _) = Locate(_playhead);
        if (!_playing && clip != null) ScheduleLiveGradePreview(clip, sourceTime);
    }

    void Tick(object? s, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double elapsed = Math.Max(0, (now - _last).TotalSeconds);
        _last = now;

        var (currentClip, _, currentStart) = Locate(_playhead);
        if (_playing && currentClip is { IsImage: false } &&
            _loadedPath == PreviewMediaPath(currentClip.Path) && Preview.NaturalVideoWidth > 0)
        {
            // Let MediaElement's decoder clock drive video playback. Re-seeking it from a
            // second stopwatch every few hundred milliseconds causes visible judder,
            // especially with low-frame-rate screen recordings and proxy files.
            double mediaTime = Preview.Position.TotalSeconds;
            if (mediaTime >= currentClip.Out - 0.04)
                _playhead = currentStart + currentClip.Duration;
            else if (mediaTime >= currentClip.In - 0.04)
                _playhead = currentStart + Math.Clamp((mediaTime - currentClip.In) / currentClip.Speed, 0, currentClip.Duration);
            else
                _playhead += elapsed;
        }
        else _playhead += elapsed;

        if (_playhead >= Extent) { _playhead = Extent; Pause(); OnPlayheadMoved(); return; }

        var (clip, src, _) = Locate(_playhead);
        if (clip is { IsGap: true })
        {
            if (_loadedPath != GapPreviewToken) ShowFrame(true);
        }
        else if (clip != null)
        {
            if (!ReferenceEquals(_previewClip, clip) || _loadedPath != PreviewMediaPath(clip.Path)) ShowFrame(true);
            else if (!clip.IsImage)
            {
                if (Math.Abs(Preview.SpeedRatio - clip.Speed) > 0.001) Preview.SpeedRatio = clip.Speed;
            }
        }
        else if (_loadedPath != null && _playhead >= Total - 0.001 && _playhead < Extent - 0.001)
            ShowLastVideoFrame();
        else if (_loadedPath != null) ShowFrame(false);
        OnPlayheadMoved();
    }

    void ShowLastVideoFrame()
    {
        if (_timeline.Count == 0) return;
        var last = _timeline[^1];
        _previewClip = last.IsGap ? null : last;
        UpdateMainFrameTransform(last.IsGap ? null : last);
        WelcomePanel.Visibility = Visibility.Collapsed;
        ColorGradePreviewImage.Visibility = Visibility.Collapsed;
        if (last.IsGap)
        {
            Preview.Stop(); Preview.Source = null; PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed; PreviewVideoLayer.Background = Brushes.Black;
            _loadedPath = GapPreviewToken;
            return;
        }
        PreviewVideoLayer.Background = Brushes.Transparent;
        if (last.IsImage)
        {
            if (_loadedPath != last.Path)
            {
                _loadedPath = last.Path;
                Preview.Source = null;
                try { PreviewImage.Source = LoadImage(last.Path); } catch { PreviewImage.Source = null; }
            }
            PreviewImage.Visibility = Visibility.Visible;
            return;
        }

        PreviewImage.Visibility = Visibility.Collapsed;
        string path = PreviewMediaPath(last.Path);
        double lastFrame = Math.Max(last.In, last.Out - 0.05);
        if (_loadedPath != path)
        {
            _loadedPath = path;
            _pendingSeek = lastFrame;
            Preview.Source = new Uri(path);
            Preview.Play(); // MediaOpened seeks to the final frame, then pauses while the audio tail plays.
            return;
        }
        if (Math.Abs(Preview.Position.TotalSeconds - lastFrame) > 0.12)
            Preview.Position = TimeSpan.FromSeconds(lastFrame);
        Preview.Pause();
    }

    void Play()
    {
        if (_timeline.Count == 0 && _audios.Count == 0) return;
        if (_playhead >= Extent - 0.01) _playhead = 0;
        _playing = true; _last = DateTime.UtcNow;
        ColorGradePreviewImage.Visibility = Visibility.Collapsed;
        _livePreviewCts?.Cancel(); _livePreviewCts = null;
        PreviewPlayBtn.Content = "❚❚";
        ShowFrame(true);
        SyncAudioPreview();
        _timer.Start();
    }

    void Pause()
    {
        bool wasPlaying = _playing;
        _playing = false; _timer.Stop();
        _audioPlaybackActive.Clear();
        Preview.Pause();
        foreach (var player in _audioPlayers.Values) player.Pause();
        PreviewPlayBtn.Content = "▶";
        if (wasPlaying)
        {
            var (clip, sourceTime, _) = Locate(_playhead);
            if (clip != null) ScheduleLiveGradePreview(clip, sourceTime);
        }
    }

    void SyncAudioPreview()
    {
        foreach (var old in _audioPlayers.Keys.Where(a => !_audios.Contains(a)).ToList())
        {
            _audioPlayers[old].Stop();
            AudioPreviewHost.Children.Remove(_audioPlayers[old]);
            _audioPlayers.Remove(old);
        }
        if (!_playing)
        {
            _audioPlaybackActive.Clear();
            foreach (var player in _audioPlayers.Values) player.Pause();
            return;
        }

        foreach (var audio in _audios)
        {
            bool active = _playhead >= audio.Start && _playhead < audio.Start + audio.Duration;
            if (!active)
            {
                _audioPlaybackActive.Remove(audio);
                if (_audioPlayers.TryGetValue(audio, out var idle)) idle.Pause();
                continue;
            }
            if (!_audioPlayers.TryGetValue(audio, out var player))
            {
                player = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    Width = 1,
                    Height = 1,
                    Volume = Math.Clamp(audio.Volume, 0, 1)
                };
                _audioPlayers[audio] = player;
                AudioPreviewHost.Children.Add(player);
                player.MediaOpened += (_, _) => SyncAudioPlayer(audio, player);
                player.Source = new Uri(audio.Path);
            }
            SyncAudioPlayer(audio, player);
        }
    }

    void SyncAudioPlayer(AudioClip audio, MediaElement player)
    {
        if (!_playing || _playhead < audio.Start || _playhead >= audio.Start + audio.Duration)
        {
            _audioPlaybackActive.Remove(audio);
            player.Pause();
            return;
        }
        double sourceTime = audio.In + (_playhead - audio.Start);
        double elapsed = _playhead - audio.Start;
        double fadeIn = audio.FadeInSeconds <= 0 ? 1 : Math.Clamp(elapsed / audio.FadeInSeconds, 0, 1);
        double fadeOut = audio.FadeOutSeconds <= 0 ? 1 : Math.Clamp((audio.Duration - elapsed) / audio.FadeOutSeconds, 0, 1);
        player.Volume = (IsTrackMuted($"A{audio.Track}") ? 0 : Math.Clamp(audio.Volume * audio.VolumeAt(elapsed), 0, 1)) * fadeIn * fadeOut;
        if (player.NaturalDuration.HasTimeSpan &&
            Math.Abs(player.Position.TotalSeconds - sourceTime) > 0.35)
            player.Position = TimeSpan.FromSeconds(Math.Max(0, sourceTime));
        if (_audioPlaybackActive.Add(audio)) player.Play();
    }

    void TogglePlay() { if (_playing) Pause(); else Play(); }

    // ---------- editing ----------
    void AddToTimeline(MediaItem m)
    {
        Pause(); Checkpoint();
        ClearSelection();
        if (m.Kind == MediaKind.Audio)
        {
            var a = new AudioClip { Path = m.Path, Start = _playhead, In = 0, Duration = m.Duration, SourceDuration = m.Duration, Track = _activeAudioTrack };
            _audios.Add(a); _selAu = a;
            _ = EnsureAudioWaveformAsync(a);
        }
        else
        {
            var c = new Clip
            {
                Path = m.Path, SourceDuration = m.Duration, In = 0, Out = m.Duration,
                HasAudio = m.HasAudio, IsImage = m.Kind == MediaKind.Image,
                PointerEvents = m.PointerEvents.Select(ClonePointerEvent).ToList(),
                AutoMouseZoom = m.AutoMouseZoom, ShowClickEffects = m.ShowClickEffects
            };
            _timeline.Add(c); _selected = c;
        }
        AfterEdit();
    }

    void OpenSourceMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (BinList.SelectedItem is not MediaItem media)
        {
            MessageBox.Show(this, "Select a video, image, or audio file in Your media first.", "Source Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_sourceMonitor is { IsVisible: true }) { _sourceMonitor.Activate(); return; }
        _sourceMonitor = new SourceMonitorWindow(media) { Owner = this };
        _sourceMonitor.SendToTimeline += InsertSourceRange;
        _sourceMonitor.Show();
    }

    void InsertSourceRange(MediaItem media, double sourceIn, double sourceOut, bool video, bool audio, bool overwrite)
    {
        double duration = Math.Max(.05, Math.Min(media.Duration, sourceOut) - Math.Max(0, sourceIn));
        if (duration <= .05) { Status.Text = "Set a source range longer than 0.05 seconds"; return; }
        Pause(); Checkpoint();
        double at = Math.Clamp(_playhead, 0, Total);

        if (video && media.Kind != MediaKind.Audio)
        {
            int startIndex = EnsureMainBoundary(at);
            int insertIndex = startIndex;
            if (overwrite)
            {
                int endIndex = EnsureMainBoundary(Math.Min(Total, at + duration));
                startIndex = EnsureMainBoundary(at);
                endIndex = EnsureMainBoundary(Math.Min(Total, at + duration));
                if (endIndex > startIndex) _timeline.RemoveRange(startIndex, endIndex - startIndex);
                insertIndex = startIndex;
            }
            var clip = new Clip
            {
                Path = media.Path, SourceDuration = media.Duration, In = Math.Max(0, sourceIn), Out = Math.Min(media.Duration, sourceOut),
                HasAudio = audio && media.HasAudio, IsImage = media.Kind == MediaKind.Image,
                PointerEvents = media.PointerEvents.Select(ClonePointerEvent).ToList(), AutoMouseZoom = media.AutoMouseZoom,
                ShowClickEffects = media.ShowClickEffects
            };
            _timeline.Insert(Math.Clamp(insertIndex, 0, _timeline.Count), clip);
            ClearSelection(); _selected = clip;
        }
        else if (audio)
        {
            var clip = new AudioClip { Path = media.Path, Start = at, In = Math.Max(0, sourceIn), Duration = duration, SourceDuration = media.Duration, Track = _activeAudioTrack };
            _audios.Add(clip); ClearSelection(); _selAu = clip;
            _ = EnsureAudioWaveformAsync(clip);
            if (overwrite) Status.Text = "Source audio added at the playhead; audio overwrite is not available yet";
        }
        else return;

        AfterEdit();
        Status.Text = overwrite && video ? "Source range overwrote the video track at the playhead" : "Source range inserted at the playhead";
        _sourceMonitor?.Activate();
    }

    int EnsureMainBoundary(double time)
    {
        double cursor = 0;
        for (int i = 0; i < _timeline.Count; i++)
        {
            var c = _timeline[i];
            double end = cursor + c.Duration;
            if (time <= cursor + .01) return i;
            if (time < end - .01)
            {
                double local = time - cursor;
                if (c.IsGap)
                {
                    var leftGap = c.Clone(); leftGap.GapDuration = local;
                    var rightGap = c.Clone(); rightGap.GapDuration = c.GapDuration - local;
                    _timeline[i] = leftGap; _timeline.Insert(i + 1, rightGap);
                }
                else
                {
                    double cut = c.In + local * c.Speed;
                    var left = c.Clone(); left.Out = cut;
                    var right = c.Clone(); right.In = cut; right.TransitionIn = "None";
                    ClipMotion.SplitKeys(left, right, local);
                    _timeline[i] = left; _timeline.Insert(i + 1, right);
                }
                return i + 1;
            }
            cursor = end;
        }
        return _timeline.Count;
    }

    void AddAsOverlay(MediaItem m)
    {
        if (m.Kind == MediaKind.Audio) { MessageBox.Show("Audio can't be an overlay. Use 'Add to timeline' for audio."); return; }
        Pause(); Checkpoint();
        ClearSelection();
        var o = new OverlayClip
        {
            Path = m.Path,
            IsImage = m.Kind == MediaKind.Image,
            Start = _playhead,
            Duration = m.Duration,
            SourceDuration = m.Duration,
            AspectRatio = m.Height > 0 ? (double)m.Width / m.Height : 16.0 / 9.0
        };
        _overlays.Add(o); _selOv = o;
        AfterEdit();
    }

    void AddTextClip()
    {
        Pause(); Checkpoint();
        ClearSelection();
        var o = new OverlayClip { IsText = true, Text = "Your text", Start = _playhead, Duration = 3, X = 0.5, Y = 0.85 };
        AssignTextTracks(new[] { o });
        _overlays.Add(o); _selOv = o;
        AfterEdit();
    }

    void AssignTextTracks(IEnumerable<OverlayClip> clips)
    {
        var batch = clips.ToList();
        var batchSet = batch.ToHashSet();
        var occupied = _overlays.Where(o => o.IsText && !batchSet.Contains(o)).ToList();
        foreach (var clip in batch.OrderBy(o => o.Start))
        {
            int bestTrack = 1, bestOverlapCount = int.MaxValue;
            for (int track = 1; track <= _textLaneCount; track++)
            {
                int overlaps = occupied.Count(o => o.TextTrack == track && o.Start < clip.End - 0.01 && o.End > clip.Start + 0.01);
                if (overlaps < bestOverlapCount) { bestOverlapCount = overlaps; bestTrack = track; }
                if (overlaps == 0) break;
            }
            if (bestOverlapCount > 0 && _textLaneCount < 12)
            {
                _textLaneCount++;
                bestTrack = _textLaneCount;
            }
            clip.TextTrack = bestTrack;
            occupied.Add(clip);
        }
    }

    void Split()
    {
        if (SelectionHasLockedTrack()) { Status.Text = "Unlock the selected track before splitting clips"; return; }
        if (_selAu is { } audio && _audios.Contains(audio))
        {
            double localAudio = _playhead - audio.Start;
            if (localAudio < 0.05 || localAudio > audio.Duration - 0.05) { Status.Text = "Move the playhead inside the selected audio clip to split it"; return; }
            Pause(); Checkpoint();
            var rightAudio = audio.Clone();
            audio.Duration = localAudio;
            rightAudio.Start += localAudio; rightAudio.In += localAudio; rightAudio.Duration -= localAudio;
            int audioIndex = _audios.IndexOf(audio);
            _audios.Insert(audioIndex + 1, rightAudio);
            ClearSelection(); _selAu = rightAudio;
            AfterEdit();
            Status.Text = "Audio split at the playhead";
            return;
        }
        if (_selOv is { IsText: true } text && _overlays.Contains(text))
        {
            double localText = _playhead - text.Start;
            if (localText < 0.05 || localText > text.Duration - 0.05) { Status.Text = "Move the playhead inside the selected text clip to split it"; return; }
            Pause(); Checkpoint();
            var rightText = text.Clone();
            text.Duration = localText;
            rightText.Start += localText; rightText.Duration -= localText;
            int textIndex = _overlays.IndexOf(text);
            _overlays.Insert(textIndex + 1, rightText);
            ClearSelection(); _selOv = rightText;
            AfterEdit();
            Status.Text = "Text clip split at the playhead";
            return;
        }
        var (clip, _, start) = Locate(_playhead);
        if (clip == null) return;
        double local = _playhead - start;
        if (local < 0.05 || local > clip.Duration - 0.05) return;
        Pause(); Checkpoint();
        if (clip.IsGap)
        {
            double leftDuration = Math.Clamp(local, 0.1, clip.Duration - 0.1);
            var rightGap = new Clip { IsGap = true, GapDuration = clip.Duration - leftDuration };
            clip.GapDuration = leftDuration;
            int gapIndex = _timeline.IndexOf(clip);
            _timeline.Insert(gapIndex + 1, rightGap);
            ClearSelection(); _selected = rightGap;
            AfterEdit();
            return;
        }
        double cut = clip.In + local * clip.Speed;
        var left = clip.Clone(); left.Out = cut;
        var right = clip.Clone(); right.In = cut; right.TransitionIn = "None";
        ClipMotion.SplitKeys(left, right, local);
        int i = _timeline.IndexOf(clip);
        _timeline[i] = left; _timeline.Insert(i + 1, right);
        ClearSelection(); _selected = right;
        AfterEdit();
    }

    void InsertGap_Click(object sender, RoutedEventArgs e)
    {
        const double gapLength = 1.0;
        Pause();
        Checkpoint();
        double insertAt = Math.Clamp(_playhead, 0, Total);
        int insertIndex = _timeline.Count;
        double cursor = 0;
        for (int i = 0; i < _timeline.Count; i++)
        {
            var clip = _timeline[i];
            double end = cursor + clip.Duration;
            if (insertAt <= cursor + 0.01) { insertIndex = i; break; }
            if (insertAt < end - 0.01)
            {
                double local = insertAt - cursor;
                if (clip.IsGap)
                {
                    var rightGap = new Clip { IsGap = true, GapDuration = clip.Duration - local };
                    clip.GapDuration = local;
                    _timeline.Insert(i + 1, rightGap);
                }
                else
                {
                    double sourceCut = clip.In + local * clip.Speed;
                    var rightClip = clip.Clone();
                    rightClip.In = sourceCut;
                    rightClip.TransitionIn = "None";
                    ClipMotion.SplitKeys(clip, rightClip, local);
                    clip.Out = sourceCut;
                    _timeline.Insert(i + 1, rightClip);
                }
                insertIndex = i + 1;
                break;
            }
            cursor = end;
            if (Math.Abs(insertAt - end) <= 0.01) { insertIndex = i + 1; break; }
        }

        var gap = new Clip { IsGap = true, GapDuration = gapLength };
        _timeline.Insert(Math.Clamp(insertIndex, 0, _timeline.Count), gap);
        foreach (var overlay in _overlays.Where(o => o.Start >= insertAt - 0.001)) overlay.Start += gapLength;
        foreach (var audio in _audios.Where(a => a.Start >= insertAt - 0.001)) audio.Start += gapLength;
        ClearSelection(); _selected = gap;
        AfterEdit();
        Status.Text = "Inserted a 1-second black gap · select it to change its length";
    }

    void AddTextLane_Click(object sender, RoutedEventArgs e)
    {
        if (_textLaneCount >= MaxTimelineLanes) { Status.Text = $"The timeline supports up to {MaxTimelineLanes} text lanes"; return; }
        Checkpoint(); _textLaneCount++; Refresh(); ScrollTimelineToBottom();
        Status.Text = $"Added T{_textLaneCount} text lane";
    }

    void AddAudioLane_Click(object sender, RoutedEventArgs e)
    {
        if (_audioLaneCount >= MaxTimelineLanes) { Status.Text = $"The timeline supports up to {MaxTimelineLanes} audio lanes"; return; }
        Checkpoint(); _audioLaneCount++; Refresh(); ScrollTimelineToBottom();
        Status.Text = $"Added A{_audioLaneCount} audio lane";
    }

    void ScrollTimelineToBottom()
    {
        TimelineScroll.UpdateLayout();
        TimelineScroll.ScrollToVerticalOffset(TimelineScroll.ScrollableHeight);
    }

    void CompactTimelineLanes()
    {
        var textTracks = _overlays.Where(o => o.IsText).Select(o => Math.Clamp(o.TextTrack, 1, MaxTimelineLanes)).Distinct().Order().ToList();
        var textMap = textTracks.Select((old, index) => (old, next: index + 1)).ToDictionary(x => x.old, x => x.next);
        foreach (var overlay in _overlays.Where(o => o.IsText)) overlay.TextTrack = textMap[Math.Clamp(overlay.TextTrack, 1, MaxTimelineLanes)];
        _textLaneCount = Math.Max(1, textTracks.Count);

        var audioTracks = _audios.Select(a => Math.Clamp(a.Track, 1, MaxTimelineLanes)).Distinct().Order().ToList();
        var audioMap = audioTracks.Select((old, index) => (old, next: index + 1)).ToDictionary(x => x.old, x => x.next);
        foreach (var audio in _audios) audio.Track = audioMap[Math.Clamp(audio.Track, 1, MaxTimelineLanes)];
        _audioLaneCount = Math.Max(1, audioTracks.Count);
    }

    void CleanEmptyLanes_Click(object sender, RoutedEventArgs e)
    {
        int oldTextCount = _textLaneCount, oldAudioCount = _audioLaneCount;
        bool alreadyCompact = _overlays.Where(o => o.IsText).Select(o => o.TextTrack).Distinct().Order().SequenceEqual(Enumerable.Range(1, _overlays.Where(o => o.IsText).Select(o => o.TextTrack).Distinct().Count()))
            && _audios.Select(a => a.Track).Distinct().Order().SequenceEqual(Enumerable.Range(1, _audios.Select(a => a.Track).Distinct().Count()));
        if (alreadyCompact && _textLaneCount == Math.Max(1, _overlays.Where(o => o.IsText).Select(o => o.TextTrack).DefaultIfEmpty(1).Max())
            && _audioLaneCount == Math.Max(1, _audios.Select(a => a.Track).DefaultIfEmpty(1).Max()))
        { Status.Text = "No empty timeline lanes to remove"; return; }
        Checkpoint(); CompactTimelineLanes(); Refresh();
        Status.Text = $"Removed unused lanes · Text {oldTextCount}→{_textLaneCount}, Audio {oldAudioCount}→{_audioLaneCount}";
    }

    void GapDuration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _selected is not { IsGap: true } gap) return;
        double next = Math.Clamp(GapDurationSlider.Value, GapDurationSlider.Minimum, GapDurationSlider.Maximum);
        if (Math.Abs(next - gap.GapDuration) < 0.005) return;
        ResizeGap(gap, next);
        AfterEdit();
    }

    void ResizeGap(Clip gap, double nextDuration)
    {
        int index = _timeline.IndexOf(gap);
        if (index < 0) return;
        double start = _timeline.Take(index).Sum(c => c.Duration);
        double oldEnd = start + gap.Duration;
        double delta = nextDuration - gap.Duration;
        gap.GapDuration = nextDuration;
        foreach (var overlay in _overlays.Where(o => o.Start >= oldEnd - 0.001)) overlay.Start = Math.Max(start, overlay.Start + delta);
        foreach (var audio in _audios.Where(a => a.Start >= oldEnd - 0.001)) audio.Start = Math.Max(start, audio.Start + delta);
        if (_playhead >= oldEnd) _playhead = Math.Max(start, _playhead + delta);
    }

    void RippleDelete_Click(object sender, RoutedEventArgs e) => RippleDeleteSelectedMainClip();

    void RippleDeleteSelectedMainClip()
    {
        var clips = _multiSelection.OfType<Clip>().Where(_timeline.Contains).ToList();
        if (clips.Count == 0 && _selected is { } single && _timeline.Contains(single)) clips.Add(single);
        if (clips.Count == 0)
        {
            Status.Text = "Select one or more V1 clips to ripple-delete";
            return;
        }

        Pause();
        Checkpoint();
        var removeItems = new HashSet<object>(_multiSelection);
        foreach (var clip in clips) removeItems.Add(clip);
        RippleDeleteMainClips(clips, removeItems);
    }

    void RippleDeleteMainClips(IReadOnlyCollection<Clip> clips, HashSet<object>? alsoDelete = null)
    {
        var targets = _timeline.Where(clips.Contains).ToHashSet();
        if (targets.Count == 0) return;
        var cuts = new List<(double Start, double End)>();
        double cursor = 0;
        foreach (var clip in _timeline)
        {
            double end = cursor + clip.Duration;
            if (targets.Contains(clip)) cuts.Add((cursor, end));
            cursor = end;
        }
        double removed = cuts.Sum(c => c.End - c.Start);
        _timeline.RemoveAll(targets.Contains);

        foreach (var overlay in _overlays.ToList())
        {
            if (alsoDelete?.Contains(overlay) == true) { _overlays.Remove(overlay); continue; }
            foreach (var cut in cuts.OrderByDescending(c => c.Start))
            {
                var mapped = RippleMap(overlay.Start, overlay.Duration, overlay.In, cut.Start, cut.End);
                if (mapped.remove) { _overlays.Remove(overlay); break; }
                overlay.Start = mapped.start; overlay.Duration = mapped.duration; overlay.In = mapped.sourceIn;
            }
        }
        foreach (var audio in _audios.ToList())
        {
            if (alsoDelete?.Contains(audio) == true) { _audios.Remove(audio); continue; }
            foreach (var cut in cuts.OrderByDescending(c => c.Start))
            {
                var mapped = RippleMap(audio.Start, audio.Duration, audio.In, cut.Start, cut.End);
                if (mapped.remove) { _audios.Remove(audio); break; }
                audio.Start = mapped.start; audio.Duration = mapped.duration; audio.In = mapped.sourceIn;
            }
        }
        foreach (var cut in cuts.OrderByDescending(c => c.Start))
        {
            if (_playhead >= cut.End - 0.001) _playhead -= cut.End - cut.Start;
            else if (_playhead > cut.Start) _playhead = cut.Start;
        }
        CompactTimelineLanes();
        ClearSelection();
        AfterEdit();
        Status.Text = $"Removed {targets.Count} V1 clip(s) · closed {Fmt(removed, true)} of timeline";
    }

    static (double start, double duration, double sourceIn, bool remove) RippleMap(
        double itemStart, double itemDuration, double sourceIn, double cutStart, double cutEnd)
    {
        const double epsilon = 0.001;
        double itemEnd = itemStart + itemDuration;
        if (itemEnd <= cutStart + epsilon) return (itemStart, itemDuration, sourceIn, false);
        if (itemStart >= cutEnd - epsilon) return (itemStart - (cutEnd - cutStart), itemDuration, sourceIn, false);

        double left = Math.Max(0, cutStart - itemStart);
        double right = Math.Max(0, itemEnd - cutEnd);
        if (left <= epsilon && right <= epsilon) return (itemStart, 0, sourceIn, true);
        if (left > epsilon) return (itemStart, left, sourceIn, false);
        return (cutStart, right, sourceIn + Math.Max(0, cutEnd - itemStart), false);
    }

    void DeleteSelected()
    {
        if (!HasSelection) return;
        if (SelectionHasLockedTrack()) { Status.Text = "Unlock the selected track before deleting clips"; return; }
        if (_multiSelection.Count <= 1 && _selected is { IsGap: true })
        {
            RippleDeleteSelectedMainClip();
            return;
        }
        Pause(); Checkpoint();
        if (_multiSelection.Count > 1)
        {
            var selectedMain = _multiSelection.OfType<Clip>().Where(_timeline.Contains).ToList();
            if (selectedMain.Count > 0)
            {
                Pause(); Checkpoint();
                RippleDeleteMainClips(selectedMain, new HashSet<object>(_multiSelection));
                return;
            }
            foreach (var item in _multiSelection.ToArray())
            {
                if (item is Clip clip) _timeline.Remove(clip);
                else if (item is OverlayClip overlay) _overlays.Remove(overlay);
                else if (item is AudioClip audio) _audios.Remove(audio);
            }
            int count = _multiSelection.Count;
            CompactTimelineLanes();
            ClearSelection();
            AfterEdit();
            Status.Text = $"Deleted {count} selected clips";
            return;
        }
        if (_selectedZoomEvent != null && _zoomEventClip != null)
        {
            _zoomEventClip.PointerEvents.Remove(_selectedZoomEvent);
            if (_zoomEventClip.PointerEvents.Count == 0) _zoomEventClip.AutoMouseZoom = false;
            _selected = _zoomEventClip;
            _selectedZoomEvent = null; _zoomEventClip = null;
            AfterEdit();
            return;
        }
        if (_selected != null)
        {
            RippleDeleteMainClips(new[] { _selected });
            return;
        }
        if (_selOv != null) _overlays.Remove(_selOv);
        if (_selAu != null) _audios.Remove(_selAu);
        CompactTimelineLanes();
        ClearSelection();
        AfterEdit();
    }

    void DuplicateSelected()
    {
        if (!HasSelection) return;
        if (SelectionHasLockedTrack()) { Status.Text = "Unlock the selected track before duplicating clips"; return; }
        Pause();
        Checkpoint();
        if (_multiSelection.Count > 1)
        {
            var copies = new List<object>();
            var selectedMain = _timeline.Where(c => _multiSelection.Contains(c)).ToList();
            if (selectedMain.Count > 0)
            {
                int insert = _timeline.IndexOf(selectedMain[^1]) + 1;
                var clones = selectedMain.Select(c => c.Clone()).ToList();
                _timeline.InsertRange(insert, clones);
                copies.AddRange(clones);
            }

            var selectedOverlays = _overlays.Where(o => _multiSelection.Contains(o)).ToList();
            if (selectedOverlays.Count > 0)
            {
                double span = selectedOverlays.Max(o => o.End) - selectedOverlays.Min(o => o.Start);
                foreach (var original in selectedOverlays) { var copy = original.Clone(); copy.Start += span; _overlays.Add(copy); copies.Add(copy); }
            }

            var selectedAudio = _audios.Where(a => _multiSelection.Contains(a)).ToList();
            if (selectedAudio.Count > 0)
            {
                double span = selectedAudio.Max(a => a.Start + a.Duration) - selectedAudio.Min(a => a.Start);
                foreach (var original in selectedAudio) { var copy = original.Clone(); copy.Start += span; _audios.Add(copy); copies.Add(copy); }
            }
            ClearSelection();
            foreach (var copy in copies) _multiSelection.Add(copy);
            if (copies.Count > 0) FocusTimelineItem(copies[0]);
            AfterEdit();
            Status.Text = $"Duplicated {copies.Count} clips";
            return;
        }
        if (_selected is { } clip)
        {
            int index = _timeline.IndexOf(clip);
            var copy = clip.Clone();
            _timeline.Insert(index + 1, copy);
            _selected = copy;
        }
        else if (_selOv is { } overlay)
        {
            var copy = overlay.Clone();
            copy.Start += copy.Duration;
            _overlays.Add(copy);
            _selOv = copy;
        }
        else if (_selAu is { } audio)
        {
            var copy = audio.Clone();
            copy.Start += copy.Duration;
            _audios.Add(copy);
            _selAu = copy;
        }
        AfterEdit();
    }

    void DeleteCurrentSelection()
    {
        if (HasSelection) DeleteSelected();
        else if (BinList.SelectedItem is MediaItem) RemoveMedia_Click(this, new RoutedEventArgs());
    }

    void RemoveMedia_Click(object sender, RoutedEventArgs e)
    {
        if (BinList.SelectedItem is not MediaItem media) return;
        _bin.Remove(media);
        Status.Text = "Removed from media library (source file kept)";
    }

    void BinList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(BinList, source) is ListBoxItem row)
            BinList.SelectedItem = row.DataContext;
    }

    void BinList_DragStart(object sender, MouseButtonEventArgs e)
    {
        _binDragPending = e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(BinList, source) is ListBoxItem;
        if (_binDragPending) _binDragStart = e.GetPosition(BinList);
    }

    void BinList_DragMove(object sender, MouseEventArgs e)
    {
        if (!_binDragPending || e.LeftButton != MouseButtonState.Pressed || BinList.SelectedItem is not MediaItem media) return;
        Point current = e.GetPosition(BinList);
        if (Math.Abs(current.X - _binDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _binDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _binDragPending = false;
        DragDrop.DoDragDrop(BinList, new DataObject(typeof(MediaItem), media), DragDropEffects.Copy);
    }

    void Timeline_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(MediaItem)) || e.Data.GetDataPresent(typeof(DesignDrag)))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else e.Effects = DragDropEffects.None;
    }

    void Timeline_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DesignDrag)) is DesignDrag design)
        {
            DropDesignOnTimeline(design, e.GetPosition(TimelineCanvas));
            e.Handled = true;
            return;
        }
        if (e.Data.GetData(typeof(MediaItem)) is not MediaItem media) return;
        Point point = e.GetPosition(TimelineCanvas);
        if (point.X < TrackLabelWidth) return;
        double time = Math.Max(0, (point.X - TrackLabelWidth) / Pps);
        if (media.Kind != MediaKind.Audio && point.Y >= LaneAudioY && !media.HasAudio)
        {
            Status.Text = "This video has no audio track to add";
            return;
        }
        Pause(); Checkpoint(); ClearSelection();

        if (media.Kind == MediaKind.Audio || point.Y >= LaneAudioY)
        {
            if (media.Kind == MediaKind.Audio || media.HasAudio)
            {
                var audio = new AudioClip { Path = media.Path, Start = time, In = 0, Duration = media.Duration, SourceDuration = media.Duration, Track = AudioTrackAt(point.Y) };
                _audios.Add(audio); _selAu = audio;
                _ = EnsureAudioWaveformAsync(audio);
            }
            else return;
        }
        else if (point.Y >= LaneMainY && point.Y < LaneOvY && media.Kind != MediaKind.Audio)
        {
            var clip = new Clip { Path = media.Path, SourceDuration = media.Duration, In = 0, Out = media.Duration, HasAudio = media.HasAudio, IsImage = media.Kind == MediaKind.Image };
            int index = MainDropIndex(time + clip.Duration / 2, null);
            _timeline.Insert(Math.Clamp(index, 0, _timeline.Count), clip);
            _selected = clip;
        }
        else if (point.Y >= LaneOvY && point.Y < LaneTextY && media.Kind != MediaKind.Audio)
        {
            var overlay = new OverlayClip
            {
                Path = media.Path, IsImage = media.Kind == MediaKind.Image, Start = time,
                Duration = media.Duration, SourceDuration = media.Duration,
                AspectRatio = media.Height > 0 ? (double)media.Width / media.Height : 16.0 / 9.0,
                VideoTrack = VideoTrackAt(point.Y)
            };
            _overlays.Add(overlay); _selOv = overlay;
        }
        else
        {
            Status.Text = "Drop video on V1/V2–V4, or drop audio on A1–A3";
            return;
        }

        AfterEdit();
        Status.Text = $"Added {media.Name} to timeline";
    }

    void BinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || BinList.SelectedItem == null) return;
        ClearSelection();
        LoadInspector();
        Refresh();
    }

    void Timeline_RightClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(TimelineCanvas);
        if (point.X < TrackLabelWidth) { e.Handled = true; return; }
        double time = SnapToFrame(Math.Clamp((point.X - TrackLabelWidth) / Pps, 0, Math.Max(Extent, 0.001)));
        BinList.SelectedItem = null;
        ClearSelection();
        if (point.Y >= LaneOvY && point.Y < LaneOv3Y)
        {
            foreach (var entry in _zoomMarkerOwners)
            {
                if (!_timelineBlocks.TryGetValue(entry.Key, out var marker)) continue;
                double left = Canvas.GetLeft(marker), top = Canvas.GetTop(marker);
                if (point.X < left || point.X > left + marker.Width || point.Y < top || point.Y > top + marker.Height) continue;
                _selectedZoomEvent = entry.Key; _zoomEventClip = entry.Value; _selected = entry.Value;
                time = _timeline.TakeWhile(c => c != entry.Value).Sum(c => c.Duration) + (entry.Key.TimeSeconds - entry.Value.In) / entry.Value.Speed;
                break;
            }
        }
        if (point.Y >= LaneAudioY)
        {
            _activeAudioTrack = AudioTrackAt(point.Y);
            _selAu = _audios.LastOrDefault(a => a.Track == AudioTrackAt(point.Y) && time >= a.Start && time <= a.Start + a.Duration);
        }
        else if (point.Y >= LaneTextY && point.Y < LaneAudioY)
            _selOv = _overlays.LastOrDefault(o => o.IsText && o.TextTrack == TextTrackAt(point.Y) && time >= o.Start && time <= o.End);
        else if (_selectedZoomEvent == null && point.Y >= LaneOvY && point.Y < LaneTextY)
            _selOv = _overlays.LastOrDefault(o => !o.IsText && o.VideoTrack == VideoTrackAt(point.Y) && time >= o.Start && time <= o.End);
        else if (point.Y >= LaneMainY)
            _selected = Locate(time).clip;
        _playhead = time;
        Refresh();
        LoadInspector();
        ShowFrame(_playing);
        OnPlayheadMoved();
    }

    void Move(int dir)
    {
        if (_selected == null) return;
        int i = _timeline.IndexOf(_selected), j = i + dir;
        if (i < 0 || j < 0 || j >= _timeline.Count) return;
        Pause(); Checkpoint();
        (_timeline[i], _timeline[j]) = (_timeline[j], _timeline[i]);
        AfterEdit();
    }

    void Undo()
    {
        _textTypingUndoRecorded = false;
        if (_undo.Count == 0) return;
        _projectDirty = true;
        Pause(); _redo.Push(Snap());
        Restore(_undo.Pop()); AfterEdit();
        UpdateProjectTitle();
    }

    void Redo()
    {
        _textTypingUndoRecorded = false;
        if (_redo.Count == 0) return;
        _projectDirty = true;
        Pause(); _undo.Push(Snap());
        Restore(_redo.Pop()); AfterEdit();
        UpdateProjectTitle();
    }

    // ---------- UI events ----------
    async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!FFmpegService.ToolsInstalled && !await EnsureMediaToolsInteractive()) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        Status.Text = "Importing...";
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                var pr = await FFmpegService.ProbeAsync(f);
                var kind = ImgExt.Contains(ext) ? MediaKind.Image : AudExt.Contains(ext) ? MediaKind.Audio : MediaKind.Video;
                var item = new MediaItem
                {
                    Path = f, Kind = kind,
                    Duration = kind == MediaKind.Image ? 5 : pr.Duration,
                    HasAudio = kind == MediaKind.Video && pr.HasAudio,
                    Width = pr.Width, Height = pr.Height
                };
                if (kind == MediaKind.Image)
                {
                    try { item.Thumbnail = LoadMediaThumbnail(f); } catch { }
                }
                _bin.Add(item);
                if (kind == MediaKind.Video) _ = LoadVideoThumbnailAsync(item);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Import failed"); }
        }
        Status.Text = "Ready";
        _projectDirty = true;
    }

    async void RecordScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecorder != null)
        {
            try { await StopScreenRecordingAsync(openInEditor: true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Recording issue", MessageBoxButton.OK, MessageBoxImage.Warning); Status.Text = "Recording stopped with an issue"; }
            return;
        }
        if (!FFmpegService.ToolsInstalled && !await EnsureMediaToolsInteractive()) return;

        if (RecordingAudioBox.Items.Count <= 1) await LoadRecordingAudioDevicesAsync();
        RecordingDevices deviceList;
        try { deviceList = await ScreenRecorderService.ListDevicesAsync(); }
        catch { deviceList = new RecordingDevices(Array.Empty<string>(), Array.Empty<string>()); }
        string? currentInput = (RecordingAudioBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var setupOptions = _recorderOptions.Clone();
        setupOptions.TrackMouse = RecordMouseCheck.IsChecked == true;
        setupOptions.TrackTyping = RecordTypingCheck.IsChecked == true;
        if (RecorderAudioSource.IsWasapiLoopback(currentInput)) setupOptions.SystemAudioDevice = RecorderAudioSource.WasapiLoopback;
        string? currentMic = string.IsNullOrWhiteSpace(currentInput) || RecorderAudioSource.IsWasapiLoopback(currentInput) ? null : currentInput;
        var setup = new RecordingSetupWindow(setupOptions, deviceList.Audio, deviceList.Video, _captureArea, _captureWindow, currentMic) { Owner = this };
        if (setup.ShowDialog() != true) return;
        _recorderOptions = setup.Options;
        _recorderOptions.Save();
        RecordMouseCheck.IsChecked = _recorderOptions.TrackMouse;
        RecordTypingCheck.IsChecked = _recorderOptions.TrackTyping;
        TrackMouseMenuItem.IsChecked = _recorderOptions.TrackMouse;
        TrackTypingMenuItem.IsChecked = _recorderOptions.TrackTyping;
        _captureArea = setup.SelectedArea;
        _captureWindow = setup.SelectedWindow;
        CaptureAreaButton.Content = _captureArea is { } pickedArea ? $"Area · {pickedArea.Width}×{pickedArea.Height}" : "Select area…";
        CaptureWindowButton.Content = _captureWindow is { } pickedWindow ? "Window: " + (pickedWindow.Title.Length > 18 ? pickedWindow.Title[..18] + "…" : pickedWindow.Title) : "Window…";
        ClearCaptureSourceButton.Visibility = _captureArea != null || _captureWindow != null ? Visibility.Visible : Visibility.Collapsed;
        int mode = RecordingModeBox.SelectedIndex;
        var options = _recorderOptions.Clone();
        options.Fps = options.FpsOverride > 0 ? options.FpsOverride : mode == 1 ? 12 : mode == 2 ? 60 : 30;
        options.TargetHeight = options.HeightOverride >= 0 ? options.HeightOverride : mode == 1 ? 540 : 0;
        options.TrackMouse = RecordMouseCheck.IsChecked == true;
        options.TrackTyping = options.TrackMouse && RecordTypingCheck.IsChecked == true && _recorderOptions.AutoZoomOnTyping;
        options.MicDevice = setup.SelectedMicrophone;
        options.SystemAudioDevice = setup.SelectedSystemAudio;
        options.WebcamDevice = setup.SelectedCamera ?? "";
        options.SystemRamGiB = InstalledRamGiB();
        string? chosenAudio = options.MicDevice;
        options.Area = setup.SelectedArea;
        options.Window = setup.SelectedWindow;

        if (_captureWindow != null && !ScreenRecorderService.IsCaptureWindowAvailable(_captureWindow))
        {
            MessageBox.Show(this, "The selected window has closed or is no longer available. Choose the window again before recording.", "Capture source unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_captureArea is { Width: < 2 } or { Height: < 2 })
        {
            MessageBox.Show(this, "The selected capture area is too small. Select an area at least 2 × 2 pixels.", "Capture area unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string sourceSummary = _captureWindow != null ? "Window: " + _captureWindow.Title
            : _captureArea is { } area ? $"Area: {area.Width} × {area.Height}"
            : $"Primary screen: {(int)SystemParameters.PrimaryScreenWidth} × {(int)SystemParameters.PrimaryScreenHeight}";
        var audioSources = new List<string>();
        if (RecorderAudioSource.IsWasapiLoopback(chosenAudio)) audioSources.Add("System audio (WASAPI)");
        else if (!string.IsNullOrWhiteSpace(chosenAudio)) audioSources.Add("Microphone: " + chosenAudio);
        if (!string.IsNullOrWhiteSpace(options.SystemAudioDevice) && !audioSources.Any(s => s.Contains("WASAPI", StringComparison.OrdinalIgnoreCase)))
            audioSources.Add("Additional audio: " + options.SystemAudioDevice);
        string audioSummary = audioSources.Count == 0 ? "Audio off" : string.Join(" + ", audioSources);
        string resolutionSummary = options.TargetHeight > 0 ? $"up to {options.TargetHeight}p" : "native screen size";
        string insertionSummary = (RecordingInsertBox.SelectedIndex) switch
        {
            0 => "at the current playhead",
            2 => "in the media library only",
            _ => "at the end of the main video track"
        };
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MyEditor Recordings");
        Directory.CreateDirectory(folder);
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            InitialDirectory = folder,
            FileName = "Screen " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".mp4"
        };
        if (dialog.ShowDialog(this) != true) return;

        var recorder = new ScreenRecorderService();
        try
        {
            _restoreEditorAfterRecording = IsVisible;
            if (_restoreEditorAfterRecording) Hide();
            await CountdownWindow.RunAsync(options.CountdownSeconds);
            await recorder.StartAsync(dialog.FileName, options);
            _screenRecorder = recorder;
            _recordingPath = dialog.FileName;
            RecordingInsertBox.IsEnabled = false;
            RecordBtn.Content = "■  Stop recording";
            RecordBtn.Background = new SolidColorBrush(Color.FromRgb(0xB3, 0x32, 0x40));
            Status.Text = $"Recording screen · {recorder.RecordingDescription}";
            _recordControlWindow = new RecordingControlWindow(dialog.FileName, recorder.RecordingDescription);
            _recordControlWindow.PauseResumeRequested += RecorderControl_PauseResumeRequested;
            _recordControlWindow.StopRequested += RecorderControl_StopRequested;
            _recordControlWindow.UpdateRecordingState(false, 0);
            _recordControlWindow.Show();
            _recordControlWindow.StartAutoHide();
            _recordTimer.Start();
        }
        catch (Exception ex)
        {
            recorder.Dispose();
            _screenRecorder = null;
            if (_restoreEditorAfterRecording) { Show(); WindowState = WindowState.Normal; Activate(); _restoreEditorAfterRecording = false; }
            MessageBox.Show(this, ex.Message, "Screen recording could not start", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status.Text = "Screen recording did not start";
        }
    }

    void OpenRecorderStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecorder != null)
        {
            MessageBox.Show(this, "Stop the current recording before changing Recorder Studio settings.", "Recording in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new RecorderStudioWindow(_recorderOptions) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _recorderOptions = dialog.Result;
        _recorderOptions.Save();
        Status.Text = "Recorder Studio settings saved";
    }

    async Task AutoStopRecordingAsync(string reason)
    {
        _recordStopping = true;
        var recorder = _screenRecorder;
        try
        {
            await StopScreenRecordingAsync(openInEditor: true);
            Status.Text = reason + " · " + Status.Text;
        }
        catch (Exception ex)
        {
            string diagnostics = recorder?.LastError ?? "";
            string detail = ex.Message;
            if (!string.IsNullOrWhiteSpace(diagnostics) && !detail.Contains(diagnostics, StringComparison.Ordinal)) detail += "\n\nFFmpeg details:\n" + diagnostics;
            MessageBox.Show(this, reason + ".\n" + detail, "Recording stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _recordStopping = false; }
    }

    async Task RecoverCaptureOrStopAsync(ScreenRecorderService recorder)
    {
        _recordStopping = true;
        try
        {
            if (await recorder.RecoverUnexpectedCaptureStopAsync())
            {
                _recordControlWindow?.UpdateRecordingState(false, recorder.ElapsedSeconds);
                Status.Text = "Recording recovered · low-load full-screen mode is active";
                return;
            }
        }
        catch { /* The normal stop path below will preserve the FFmpeg diagnostics. */ }
        finally { _recordStopping = false; }
        await AutoStopRecordingAsync("Capture ended unexpectedly");
    }

    void SelectCaptureArea_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecorder != null) return;
        var selector = new CaptureAreaWindow { Owner = this };
        if (selector.ShowDialog() != true || selector.SelectedArea is not { } area) return;
        _captureArea = area;
        _captureWindow = null;
        CaptureAreaButton.Content = $"Area · {area.Width}×{area.Height}";
        CaptureWindowButton.Content = "Window…";
        ClearCaptureSourceButton.Visibility = Visibility.Visible;
        Status.Text = "Recording area selected on the primary display";
    }

    void SelectCaptureWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecorder != null) return;
        var picker = new CaptureWindowPicker { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedWindow is not { } window) return;
        _captureWindow = window;
        _captureArea = null;
        CaptureAreaButton.Content = "Select area…";
        CaptureWindowButton.Content = "Window: " + (window.Title.Length > 18 ? window.Title[..18] + "…" : window.Title);
        ClearCaptureSourceButton.Visibility = Visibility.Visible;
        Status.Text = "Recording window selected · keep it open and visible";
    }

    void ClearCaptureArea_Click(object sender, RoutedEventArgs e)
    {
        if (_screenRecorder != null) return;
        _captureArea = null;
        _captureWindow = null;
        CaptureAreaButton.Content = "Select area…";
        CaptureWindowButton.Content = "Window…";
        ClearCaptureSourceButton.Visibility = Visibility.Collapsed;
        Status.Text = "Full primary screen selected";
    }

    async void RecorderControl_PauseResumeRequested(object? sender, EventArgs e) => await ToggleScreenRecordingPauseAsync();
    async void RecorderControl_StopRequested(object? sender, EventArgs e)
    {
        try { await StopScreenRecordingAsync(openInEditor: true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Recording issue", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async Task ToggleScreenRecordingPauseAsync()
    {
        if (_screenRecorder == null) return;
        try
        {
            if (_screenRecorder.IsPaused)
            {
                await _screenRecorder.ResumeAsync();
                Status.Text = "Recording resumed";
            }
            else
            {
                await _screenRecorder.PauseAsync();
                Status.Text = "Recording paused · Ctrl+Alt+F9 to resume";
            }
            _recordControlWindow?.UpdateRecordingState(_screenRecorder.IsPaused, _screenRecorder.ElapsedSeconds);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Recording control", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status.Text = "Recording control failed";
        }
    }

    void RegisterRecorderHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkeySource = HwndSource.FromHwnd(hwnd);
        _hotkeySource?.AddHook(RecorderHotkeyHook);
        RegisterHotKey(hwnd, HotkeyStartResume, ModControl | ModAlt, 0x78); // F9
        RegisterHotKey(hwnd, HotkeyPauseResume, ModControl | ModAlt, 0x79); // F10
        RegisterHotKey(hwnd, HotkeyStop, ModControl | ModAlt, 0x7A); // F11
        RegisterHotKey(hwnd, HotkeyShowControls, ModControl | ModAlt, 0x77); // F8
    }

    void UnregisterRecorderHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, HotkeyStartResume);
        UnregisterHotKey(hwnd, HotkeyPauseResume);
        UnregisterHotKey(hwnd, HotkeyStop);
        UnregisterHotKey(hwnd, HotkeyShowControls);
        _hotkeySource?.RemoveHook(RecorderHotkeyHook);
        _hotkeySource = null;
    }

    IntPtr RecorderHotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id is not (HotkeyStartResume or HotkeyPauseResume or HotkeyStop or HotkeyShowControls)) return IntPtr.Zero;
        handled = true;
        _ = Dispatcher.BeginInvoke(new Action(() => _ = HandleRecorderHotkeyAsync(id)));
        return IntPtr.Zero;
    }

    async Task HandleRecorderHotkeyAsync(int id)
    {
        if (id == HotkeyShowControls && _screenRecorder != null)
        {
            if (_recordControlWindow?.IsVisible == true) _recordControlWindow.Hide();
            else _recordControlWindow?.ShowTemporarily();
        }
        else if (id == HotkeyStartResume)
        {
            if (_screenRecorder == null) RecordScreen_Click(this, new RoutedEventArgs());
            else if (_screenRecorder.IsPaused) await ToggleScreenRecordingPauseAsync();
        }
        else if (id == HotkeyPauseResume && _screenRecorder != null) await ToggleScreenRecordingPauseAsync();
        else if (id == HotkeyStop && _screenRecorder != null)
        {
            try { await StopScreenRecordingAsync(openInEditor: true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Recording issue", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }

    async Task StopScreenRecordingAsync(bool openInEditor)
    {
        var recorder = _screenRecorder;
        if (recorder == null) return;
        RecordBtn.IsEnabled = false;
        Status.Text = "Finishing recording…";
        _recordTimer.Stop();
        try
        {
            var pointerEvents = await recorder.StopAsync();
            string? path = _recordingPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 4096 && openInEditor)
                await OpenRecordedVideoAsync(path, pointerEvents, RecordingInsertBox.SelectedIndex);
            else if (openInEditor)
                throw new InvalidDataException("The recording file was not created. Check FFmpeg setup and try again.");
            else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                Status.Text = "Recording saved: " + System.IO.Path.GetFileName(path);
        }
        finally
        {
            recorder.Dispose();
            _screenRecorder = null;
            _recordingPath = null;
            _recordControlWindow?.CloseAfterRecordingStops();
            _recordControlWindow = null;
            RecordBtn.IsEnabled = true;
            RecordingInsertBox.IsEnabled = true;
            RecordBtn.Content = "●  Record screen";
            RecordBtn.Background = new SolidColorBrush(Color.FromRgb(0x64, 0x2B, 0x35));
            if (_restoreEditorAfterRecording) { Show(); WindowState = WindowState.Normal; Activate(); _restoreEditorAfterRecording = false; }
            if (openInEditor && Status.Text == "Finishing recording…") Status.Text = "Recording stopped";
        }
    }

    async Task OpenRecordedVideoAsync(string path, IReadOnlyList<PointerEvent> pointerEvents, int placement)
    {
        var probe = await FFmpegService.ProbeAsync(path);
        if (!probe.HasVideo || probe.Duration <= 0) throw new InvalidDataException("The recording file has no playable video stream.");
        var media = new MediaItem
        {
            Path = path, Kind = MediaKind.Video, Duration = probe.Duration,
            HasAudio = probe.HasAudio, Width = probe.Width, Height = probe.Height,
            PointerEvents = pointerEvents.Select(ClonePointerEvent).ToList(),
            AutoMouseZoom = pointerEvents.Count > 0,
            ShowClickEffects = pointerEvents.Any(p => p.IsClick)
        };
        _bin.Add(media);
        _ = LoadVideoThumbnailAsync(media);

        if (placement == 2)
        {
            _projectDirty = true;
            Refresh();
            Status.Text = $"Recording added to media library · {pointerEvents.Count} pointer/typing focus points saved";
            return;
        }

        Pause(); Checkpoint(); ClearSelection();
        var clip = new Clip
        {
            Path = path, SourceDuration = probe.Duration, In = 0, Out = probe.Duration,
            HasAudio = probe.HasAudio,
            PointerEvents = media.PointerEvents.Select(ClonePointerEvent).ToList(),
            AutoMouseZoom = pointerEvents.Count > 0,
            ShowClickEffects = pointerEvents.Any(p => p.IsClick)
        };
        double insertAt = placement == 0 ? Math.Clamp(_playhead, 0, Total) : Total;
        InsertMainTrackClipAt(insertAt, clip);
        _selected = clip;
        _playhead = insertAt;
        AfterEdit();
        ShowFrame(false);
        Status.Text = pointerEvents.Count > 0
            ? $"Recording inserted {(placement == 0 ? "at the playhead" : "at the timeline end")} · {pointerEvents.Count} focus points · click and typing zoom enabled"
            : $"Recording inserted {(placement == 0 ? "at the playhead" : "at the timeline end")}";
    }

    static PointerEvent ClonePointerEvent(PointerEvent p) => new(p.TimeSeconds, p.X, p.Y, p.IsClick)
    {
        Button = p.Button, IsTyping = p.IsTyping, ZoomStrength = p.ZoomStrength, ZoomDurationSeconds = p.ZoomDurationSeconds,
        ZoomSpeedSeconds = p.ZoomSpeedSeconds, ZoomEasing = p.ZoomEasing
    };

    static void UpgradeLegacyZoomDefaults(IEnumerable<PointerEvent>? events)
    {
        if (events == null) return;
        foreach (var e in events)
        {
            if (Math.Abs(e.ZoomStrength - 1.18) < 0.001 && Math.Abs(e.ZoomDurationSeconds - 0.78) < 0.001 && Math.Abs(e.ZoomSpeedSeconds - 0.14) < 0.001)
            {
                e.ZoomStrength = 1.35;
                e.ZoomDurationSeconds = 1.07;
                e.ZoomSpeedSeconds = 0.22;
            }
        }
    }

    void InsertMainTrackClipAt(double time, Clip clip)
    {
        int insertIndex = _timeline.Count;
        double cursor = 0;
        for (int i = 0; i < _timeline.Count; i++)
        {
            var current = _timeline[i];
            double end = cursor + current.Duration;
            if (time <= cursor + 0.01) { insertIndex = i; break; }
            if (time < end - 0.01)
            {
                double local = time - cursor;
                if (current.IsGap)
                {
                    var rightGap = new Clip { IsGap = true, GapDuration = current.Duration - local };
                    current.GapDuration = local;
                    _timeline.Insert(i + 1, rightGap);
                }
                else
                {
                    double sourceCut = current.In + local * current.Speed;
                    var right = current.Clone(); right.In = sourceCut; right.TransitionIn = "None";
                    ClipMotion.SplitKeys(current, right, local);
                    current.Out = sourceCut;
                    _timeline.Insert(i + 1, right);
                }
                insertIndex = i + 1;
                break;
            }
            cursor = end;
            if (Math.Abs(time - end) <= 0.01) { insertIndex = i + 1; break; }
        }
        _timeline.Insert(Math.Clamp(insertIndex, 0, _timeline.Count), clip);
        foreach (var overlay in _overlays.Where(o => o.Start >= time - 0.001)) overlay.Start += clip.Duration;
        foreach (var audio in _audios.Where(a => a.Start >= time - 0.001)) audio.Start += clip.Duration;
    }

    EditorProject BuildProject() => new()
    {
        ApplicationVersion = "3.3.0",
        SourceProjectPath = _currentProjectPath ?? "",
        SavedAtUtc = DateTime.UtcNow,
        Playhead = _playhead,
        TimelineZoom = Pps,
        AspectPreset = AspectBox.SelectedIndex,
        QualityPreset = QualityBox.SelectedIndex,
        ExportVideoQuality = ComboVal(VideoQualityBox),
        ExportAudioKbps = SelectedAudioKbps,
        BackgroundMode = _backgroundMode,
        BackgroundColor = _backgroundColor,
        BackgroundImagePath = _backgroundImagePath,
        UseLowMemoryProxies = _useLowMemoryProxies,
        PreviewProxyHeight = SelectedProxyHeight,
        TextLaneCount = _textLaneCount,
        AudioLaneCount = _audioLaneCount,
        MutedTracks = new Dictionary<string, bool>(_mutedTracks, StringComparer.OrdinalIgnoreCase),
        LockedTracks = new Dictionary<string, bool>(_lockedTracks, StringComparer.OrdinalIgnoreCase),
        Media = _bin.Select(m => new ProjectMedia
        {
            Path = m.Path, Kind = m.Kind, Duration = m.Duration, HasAudio = m.HasAudio, Width = m.Width, Height = m.Height,
            PointerEvents = m.PointerEvents.Select(ClonePointerEvent).ToList(), AutoMouseZoom = m.AutoMouseZoom, ShowClickEffects = m.ShowClickEffects
        }).ToList(),
        MainTrack = CloneList(_timeline),
        Overlays = _overlays.Select(o => o.Clone()).ToList(),
        Audio = _audios.Select(a => a.Clone()).ToList()
    };

    void SaveProjectTo(string path, bool autosave = false)
    {
        var project = BuildProject();
        project.SourceProjectPath = autosave ? _currentProjectPath ?? "" : path;
        var full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, project, ProjectJson);
            stream.Flush(true);
        }
        if (autosave && File.Exists(full))
        {
            try { File.Replace(temp, full, AutosaveBackupPath, true); }
            catch (PlatformNotSupportedException) { ReplaceAutosaveWithBackup(temp, full); }
            catch (IOException) { ReplaceAutosaveWithBackup(temp, full); }
        }
        else File.Move(temp, full, true);
        if (!autosave)
        {
            _currentProjectPath = full;
            _projectDirty = false;
            DeleteRecoveryFiles();
            UpdateProjectTitle();
            Status.Text = "Project saved";
        }
    }

    static void ReplaceAutosaveWithBackup(string temp, string full)
    {
        // Keep the last known-good recovery copy if the filesystem cannot perform File.Replace.
        File.Copy(full, AutosaveBackupPath, true);
        File.Move(temp, full, true);
    }

    static void DeleteRecoveryFiles()
    {
        foreach (var path in new[] { AutosavePath, AutosaveBackupPath, AutosaveTempPath })
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    void AutoSaveProject()
    {
        if (!_projectDirty || (_timeline.Count == 0 && _overlays.Count == 0 && _audios.Count == 0 && _bin.Count == 0)) return;
        try
        {
            SaveProjectTo(AutosavePath, true);
            Status.Text = $"Auto-saved · {DateTime.Now:t}";
        }
        catch (Exception ex) { Status.Text = "Auto-save unavailable: " + ex.Message; }
    }

    void UpdateProjectTitle()
    {
        string dot = _projectDirty ? "● " : "";
        Title = _currentProjectPath == null
            ? $"{dot}MyEditor 3.3  |  Video Studio"
            : $"{dot}{System.IO.Path.GetFileNameWithoutExtension(_currentProjectPath)} — MyEditor 3.3";
    }

    void ShowShortcuts_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Space\t\tPlay / pause\nLeft / Right\t\tStep one frame\nShift + Left / Right\tStep one second\nHome / End\t\tJump to start / end\n" +
        "S\t\t\tSplit at playhead\nDelete\t\tDelete selected\nShift + Delete\t\tRipple-delete V1 clip\nCtrl + D\t\tDuplicate\n" +
        "Ctrl + A\t\tSelect all\nCtrl + Z / Y\t\tUndo / redo\nCtrl + N / O / S\t\tNew / open / save\nCtrl + E\t\tExport\n" +
        "Ctrl + Mouse wheel\tZoom the timeline",
        "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    void ShowAbout_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    bool ConfirmDiscardChanges()
    {
        if (!_projectDirty) return true;
        var answer = MessageBox.Show(this, "Save your changes before continuing?", "Unsaved project", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No)
        {
            DeleteRecoveryFiles();
            _projectDirty = false;
            return true;
        }
        return SaveProjectDialog();
    }

    bool SaveProjectDialog()
    {
        if (_currentProjectPath != null)
        {
            try { SaveProjectTo(_currentProjectPath); return true; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }
        return SaveProjectAsDialog();
    }

    bool SaveProjectAsDialog()
    {
        var dialog = new SaveFileDialog { Filter = "MyEditor project|*.myeditor", DefaultExt = ".myeditor", AddExtension = true, FileName = "My project.myeditor" };
        if (dialog.ShowDialog(this) != true) return false;
        try { SaveProjectTo(dialog.FileName); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    async Task OfferAutosaveRecoveryAsync()
    {
        var candidates = new List<(EditorProject Project, string Path)>();
        foreach (var path in new[] { AutosavePath, AutosaveBackupPath, AutosaveTempPath })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var project = JsonSerializer.Deserialize<EditorProject>(File.ReadAllText(path), ProjectJson);
                if (project != null)
                {
                    project.MainTrack ??= new(); project.Overlays ??= new(); project.Audio ??= new(); project.Media ??= new();
                    if (project.MainTrack.Count > 0 || project.Overlays.Count > 0 || project.Audio.Count > 0 || project.Media.Count > 0)
                        candidates.Add((project, path));
                }
            }
            catch { /* Try the other recovery copy before reporting a problem. */ }
        }
        if (candidates.Count == 0)
        {
            if (File.Exists(AutosavePath) || File.Exists(AutosaveBackupPath))
                MessageBox.Show(this, "MyEditor could not read its auto-save or backup file. Your regular project files were not changed.", "Recovery unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var (saved, recoveryPath) = candidates.OrderByDescending(candidate => candidate.Project.SavedAtUtc).First();
            string recoveryCopy = recoveryPath == AutosaveBackupPath ? "backup copy" : recoveryPath == AutosaveTempPath ? "unfinished auto-save copy" : "auto-save";
            var choice = MessageBox.Show(this, $"MyEditor found a {recoveryCopy} from {saved.SavedAtUtc.ToLocalTime():g}. Restore it?", "Recover your work", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes) LoadProject(saved, saved.SourceProjectPath);
            else
            {
                DeleteRecoveryFiles();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "The recovery file could not be opened: " + ex.Message, "Recovery", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await Task.CompletedTask;
    }

    void LoadProject(EditorProject project, string path)
    {
        if (project.FormatVersion > 1) throw new InvalidDataException("This project was made with a newer MyEditor project format.");
        Pause();
        _timeline = project.MainTrack ?? new();
        foreach (var clip in _timeline) UpgradeLegacyZoomDefaults(clip.PointerEvents);
        _overlays = project.Overlays ?? new();
        _textLaneCount = Math.Clamp(Math.Max(project.TextLaneCount, _overlays.Where(o => o.IsText).Select(o => o.TextTrack).DefaultIfEmpty(1).Max()), 1, MaxTimelineLanes);
        AssignTextTracks(_overlays.Where(o => o.IsText));
        _audios = project.Audio ?? new();
        _mutedTracks.Clear(); foreach (var pair in project.MutedTracks ?? new()) _mutedTracks[pair.Key] = pair.Value;
        _lockedTracks.Clear(); foreach (var pair in project.LockedTracks ?? new()) _lockedTracks[pair.Key] = pair.Value;
        _audioLaneCount = Math.Clamp(Math.Max(project.AudioLaneCount, _audios.Select(a => a.Track).DefaultIfEmpty(1).Max()), 1, MaxTimelineLanes);
        foreach (var audio in _audios) _ = EnsureAudioWaveformAsync(audio);
        _bin.Clear();
        foreach (var m in project.Media.Where(m => File.Exists(m.Path)))
        {
            UpgradeLegacyZoomDefaults(m.PointerEvents);
            var item = new MediaItem
            {
                Path = m.Path, Kind = m.Kind, Duration = m.Duration, HasAudio = m.HasAudio, Width = m.Width, Height = m.Height,
                PointerEvents = m.PointerEvents ?? new(), AutoMouseZoom = m.AutoMouseZoom, ShowClickEffects = m.ShowClickEffects
            };
            if (item.Kind == MediaKind.Image) { try { item.Thumbnail = LoadMediaThumbnail(m.Path); } catch { } }
            _bin.Add(item);
            if (item.Kind == MediaKind.Video) _ = LoadVideoThumbnailAsync(item);
        }
        _currentProjectPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _backgroundMode = string.IsNullOrWhiteSpace(project.BackgroundMode) ? "Black bars" : project.BackgroundMode;
        _backgroundColor = string.IsNullOrWhiteSpace(project.BackgroundColor) ? "#151820" : project.BackgroundColor;
        _backgroundImagePath = project.BackgroundImagePath ?? "";
        PreviewHost.Background = PreviewBackgroundBrush();
        _projectDirty = false;
        _undo.Clear(); _redo.Clear(); ClearSelection();
        ZoomSlider.Value = Math.Clamp(project.TimelineZoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _loading = true;
        SyncQuickBackgroundBox();
        _useLowMemoryProxies = project.UseLowMemoryProxies;
        PreviewQualityBox.SelectedIndex = project.PreviewProxyHeight switch { >= 720 => 1, >= 540 => 2, >= 360 => 3, _ => project.UseLowMemoryProxies ? 2 : 0 };
        ProxyModeCheck.IsChecked = _useLowMemoryProxies;
        _loading = false;
        AspectBox.SelectedIndex = Math.Clamp(project.AspectPreset, 0, AspectBox.Items.Count - 1);
        QualityBox.SelectedIndex = Math.Clamp(project.QualityPreset, 0, QualityBox.Items.Count - 1);
        SelectCombo(VideoQualityBox, project.ExportVideoQuality);
        var audioQuality = AudioQualityBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == project.ExportAudioKbps.ToString(CultureInfo.InvariantCulture));
        AudioQualityBox.SelectedItem = audioQuality ?? AudioQualityBox.Items[1];
        _playhead = Math.Clamp(project.Playhead, 0, Extent);
        UpdateProjectTitle();
        Refresh(); LoadInspector(); ShowFrame(false); OnPlayheadMoved();
        _projectDirty = false;
        var missing = project.Media.Count(m => !File.Exists(m.Path));
        Status.Text = missing == 0 ? "Project opened" : $"Project opened · {missing} media file(s) missing";
    }

    void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        Pause(); _timeline.Clear(); _overlays.Clear(); _audios.Clear(); _bin.Clear(); _undo.Clear(); _redo.Clear(); ClearSelection();
        _backgroundMode = "Black bars"; _backgroundColor = "#151820"; _backgroundImagePath = ""; PreviewHost.Background = PreviewBackgroundBrush();
        _loading = true; SyncQuickBackgroundBox(); _useLowMemoryProxies = false; PreviewQualityBox.SelectedIndex = 0; ProxyModeCheck.IsChecked = false; _loading = false;
        _textLaneCount = _audioLaneCount = 3;
        _mutedTracks.Clear(); _lockedTracks.Clear();
        _playhead = 0; _currentProjectPath = null; _projectDirty = false; UpdateProjectTitle();
        Refresh(); LoadInspector(); ShowFrame(false); OnPlayheadMoved(); Status.Text = "New project";
    }

    void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        var dialog = new OpenFileDialog { Filter = "MyEditor project|*.myeditor|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var project = JsonSerializer.Deserialize<EditorProject>(File.ReadAllText(dialog.FileName), ProjectJson)
                ?? throw new InvalidDataException("The selected file is not a valid MyEditor project.");
            LoadProject(project, dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open project", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProjectDialog();
    void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectAsDialog();
    void ProjectSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        // XAML raises SelectionChanged while controls are still being constructed.
        // Do not touch the preview until the window has completed initialization.
        if (_loading || !IsLoaded) return;
        _projectDirty = true;
        if (ReferenceEquals(sender, QualityBox))
        {
            _playhead = SnapToFrame(_playhead);
            if (_selected != null) UpdateTrimFrameInfo(_selected);
            ShowFrame(_playing);
            OnPlayheadMoved();
        }
    }

    static BitmapImage LoadMediaThumbnail(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 164;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    async Task LoadVideoThumbnailAsync(MediaItem item)
    {
        await _thumbnailSemaphore.WaitAsync();
        try
        {
            var path = await FFmpegService.CreateThumbnailAsync(item.Path);
            if (path != null && System.IO.File.Exists(path)) { item.Thumbnail = LoadMediaThumbnail(path); if (_timeline.Any(t => string.Equals(t.Path, item.Path, StringComparison.OrdinalIgnoreCase))) Refresh(); }
        }
        catch { /* A missing preview frame should not block media import. */ }
        finally { _thumbnailSemaphore.Release(); }
    }

    async Task EnsureAudioWaveformAsync(AudioClip clip)
    {
        if (!string.IsNullOrWhiteSpace(clip.WaveformPath) && File.Exists(clip.WaveformPath)) return;
        await _waveformSemaphore.WaitAsync();
        try
        {
            var path = await FFmpegService.CreateWaveformAsync(clip.Path);
            if (path == null || !File.Exists(path)) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_audios.Contains(clip)) return;
                clip.WaveformPath = path;
                Refresh();
            });
        }
        catch { /* Waveforms are a visual aid; audio editing/export must remain available if generation fails. */ }
        finally { _waveformSemaphore.Release(); }
    }

    async void Tts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TtsDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultPath == null) return;
        try
        {
            var pr = await FFmpegService.ProbeAsync(dlg.ResultPath);
            Pause(); Checkpoint();
            ClearSelection();
            var a = new AudioClip { Path = dlg.ResultPath, Start = _playhead, In = 0, Duration = pr.Duration, SourceDuration = pr.Duration, Track = _activeAudioTrack };
            _audios.Add(a); _selAu = a;
            _ = EnsureAudioWaveformAsync(a);
            AfterEdit();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Text to speech failed"); }
    }

    void ImportSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SubRip subtitles (*.srt)|*.srt|All files (*.*)|*.*", Title = "Import timed subtitles" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            string content = File.ReadAllText(dialog.FileName);
            var captions = new List<OverlayClip>();
            foreach (Match match in SrtCuePattern.Matches(content))
            {
                if (!TimeSpan.TryParseExact(match.Groups["start"].Value.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var start) ||
                    !TimeSpan.TryParseExact(match.Groups["end"].Value.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var end) || end <= start)
                    continue;

                string text = System.Net.WebUtility.HtmlDecode(match.Groups["text"].Value)
                    .Replace("\\N", "\n", StringComparison.OrdinalIgnoreCase)
                    .Replace("\r", "");
                text = Regex.Replace(text, "</?[^>]+>", "").Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                captions.Add(new OverlayClip
                {
                    IsText = true, Text = text, Start = start.TotalSeconds, Duration = (end - start).TotalSeconds,
                    X = 0.5, Y = 0.88, FontSize = 52, Color = "White", TextShape = "Highlight box"
                });
            }

            if (captions.Count == 0)
            {
                MessageBox.Show(this, "No valid timed subtitle lines were found in this SRT file.", "Import subtitles", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Pause(); Checkpoint(); ClearSelection();
            AssignTextTracks(captions);
            _overlays.AddRange(captions);
            _selOv = captions[0];
            AfterEdit();
            Status.Text = $"Imported {captions.Count} subtitle lines";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not read this subtitle file.\n\n" + ex.Message, "Import subtitles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ExportSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var captions = _overlays.Where(o => o.IsText && !string.IsNullOrWhiteSpace(o.Text)).OrderBy(o => o.Start).ToList();
        if (captions.Count == 0)
        {
            MessageBox.Show(this, "There are no text clips to export as subtitles.", "Export subtitles", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog { Filter = "SubRip subtitles (*.srt)|*.srt", DefaultExt = ".srt", AddExtension = true, FileName = "subtitles.srt", Title = "Export subtitles" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            static string Stamp(double seconds) => TimeSpan.FromMilliseconds(Math.Max(0, seconds) * 1000)
                .ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
            var entries = captions.Select((clip, index) => $"{index + 1}\r\n{Stamp(clip.Start)} --> {Stamp(clip.End)}\r\n{clip.Text.Replace("\r", "").Replace("\n", "\r\n")}");
            File.WriteAllText(dialog.FileName, string.Join("\r\n\r\n", entries) + "\r\n", new System.Text.UTF8Encoding(true));
            Status.Text = $"Exported {captions.Count} subtitle lines";
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not export subtitles.\n\n" + ex.Message, "Export subtitles", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async void LocalCaptions_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || !_selected.HasAudio)
        {
            MessageBox.Show(this, "Select a video clip with audio on the main timeline first.", "Local AI captions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var clip = _selected;

        int clipIndex = _timeline.IndexOf(clip);
        double clipStart = _timeline.Take(clipIndex).Sum(c => c.Duration);
        string wav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "myeditor-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        ExportBtn.IsEnabled = false;
        ExportBar.Visibility = Visibility.Visible;
        ExportBar.IsIndeterminate = false;
        ExportBar.Value = 0;
        try
        {
            Status.Text = LocalWhisperService.IsInstalled ? "Preparing audio…" : "Downloading local caption model…";
            if (!RuntimeModuleService.IsInstalled(RuntimeModule.MediaEngine))
                await RuntimeModuleService.EnsureAsync(RuntimeModule.MediaEngine);
            var downloadProgress = new Progress<double>(p => ExportBar.Value = p * 100);
            await RuntimeModuleService.EnsureAsync(RuntimeModule.LocalCaptionEngine, downloadProgress);

            double sourceDuration = Math.Max(0.1, clip.Out - clip.In);
            await FFmpegService.ExtractMono16kWavAsync(clip.Path, wav, clip.In, sourceDuration);
            string srt = await LocalWhisperService.TranscribeAsync(wav, new Progress<string>(s => Status.Text = s));
            var captions = new List<OverlayClip>();
            foreach (Match match in SrtCuePattern.Matches(srt))
            {
                var start = TimeSpan.ParseExact(match.Groups["start"].Value.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                var end = TimeSpan.ParseExact(match.Groups["end"].Value.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                string text = match.Groups["text"].Value.Trim().Replace("\r", "");
                if (string.IsNullOrWhiteSpace(text) || end <= start) continue;
                captions.Add(new OverlayClip
                {
                    IsText = true,
                    Text = text,
                    Start = clipStart + start.TotalSeconds / clip.Speed,
                    Duration = (end - start).TotalSeconds / clip.Speed,
                    X = 0.5,
                    Y = 0.86,
                    FontSize = 52,
                    Color = "White"
                });
            }
            StyleNewCaptions(captions);
            if (captions.Count == 0)
            {
                MessageBox.Show(this, "No spoken words were found in this clip.", "Local AI captions", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Pause();
            Checkpoint();
            ClearSelection();
            AssignTextTracks(captions);
            _overlays.AddRange(captions);
            _selOv = captions[0];
            AfterEdit();
            Status.Text = $"Added {captions.Count} captions";
        }
        catch (Exception ex)
        {
            Status.Text = "Caption generation failed";
            MessageBox.Show(this, "Local caption generation could not finish.\n\n" + ex.Message, "Local AI captions", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { System.IO.File.Delete(wav); } catch { }
            ExportBar.IsIndeterminate = false;
            ExportBar.Visibility = Visibility.Collapsed;
            ExportBtn.IsEnabled = true;
        }
    }

    void AddSelected_Click(object sender, RoutedEventArgs e) { if (BinList.SelectedItem is MediaItem m) AddToTimeline(m); }
    void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();
    void MoveSelectedToPlayhead_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionHasLockedTrack()) { Status.Text = "Unlock the selected track before moving clips"; return; }
        if (_multiSelection.Count > 1 && _multiSelection.All(item => item is OverlayClip or AudioClip))
        {
            double minimum = _multiSelection.Min(item => item switch
            {
                OverlayClip o => o.Start,
                AudioClip a => a.Start,
                _ => 0
            });
            double delta = Math.Max(0, _playhead) - minimum;
            if (Math.Abs(delta) < 0.005) return;
            Checkpoint();
            foreach (var item in _multiSelection)
            {
                if (item is OverlayClip selectedOverlay) selectedOverlay.Start = Math.Max(0, selectedOverlay.Start + delta);
                else if (item is AudioClip audio) audio.Start = Math.Max(0, audio.Start + delta);
            }
            AfterEdit();
            Status.Text = $"Moved {_multiSelection.Count} clips to the playhead";
            return;
        }
        if (_multiSelection.Count > 1 && _multiSelection.All(item => item is Clip))
        {
            var originals = _timeline.Where(c => _multiSelection.Contains(c)).ToList();
            if (originals.Count > 0)
            {
                int rawIndex = MainDropIndex(_playhead + originals.Sum(c => c.Duration) / 2, null);
                int before = originals.Count(c => _timeline.IndexOf(c) < rawIndex);
                var reordered = _timeline.Where(c => !_multiSelection.Contains(c)).ToList();
                reordered.InsertRange(Math.Clamp(rawIndex - before, 0, reordered.Count), originals);
                if (_timeline.SequenceEqual(reordered)) return;
                Checkpoint();
                _timeline = reordered;
                AfterEdit();
                Status.Text = $"Moved {originals.Count} clips on V1";
            }
            return;
        }
        if (_selOv is { } overlay)
        {
            Checkpoint(); overlay.Start = Math.Max(0, _playhead); AfterEdit();
        }
        else if (_selAu is { } audio)
        {
            Checkpoint(); audio.Start = Math.Max(0, _playhead); AfterEdit();
        }
        else if (_selected is { } clip)
        {
            int from = _timeline.IndexOf(clip), to = MainDropIndex(_playhead + clip.Duration / 2, clip);
            if (from >= 0 && to != from)
            {
                Checkpoint(); _timeline.RemoveAt(from); _timeline.Insert(Math.Clamp(to, 0, _timeline.Count), clip); AfterEdit();
            }
        }
    }
    void WelcomeImport_Click(object sender, RoutedEventArgs e) => Import_Click(sender, e);
    void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MoreMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
    void MediaSearch_TextChanged(object sender, TextChangedEventArgs e) => CollectionViewSource.GetDefaultView(_bin)?.Refresh();
    void AsOverlay_Click(object sender, RoutedEventArgs e) { if (BinList.SelectedItem is MediaItem m) AddAsOverlay(m); }
    void AddText_Click(object sender, RoutedEventArgs e) => AddTextClip();
    void BinList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (BinList.SelectedItem is MediaItem m) AddToTimeline(m); }
    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Split_Click(object sender, RoutedEventArgs e) => Split();
    void Delete_Click(object sender, RoutedEventArgs e) => DeleteCurrentSelection();
    void SelectAllTimelineItems()
    {
        var selected = _timeline.Cast<object>()
            .Concat(_overlays)
            .Concat(_audios)
            .ToList();
        ClearSelection();
        foreach (var item in selected) _multiSelection.Add(item);
        if (selected.Count > 0) FocusTimelineItem(selected[0]);
        Refresh(); LoadInspector();
        Status.Text = selected.Count == 0 ? "No clips to select" : $"Selected {selected.Count} clips · drag to move, Ctrl+D to duplicate, Delete to remove";
    }
    void MoveLeft_Click(object sender, RoutedEventArgs e) => Move(-1);
    void MoveRight_Click(object sender, RoutedEventArgs e) => Move(1);
    void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();
    void PreviewPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void PreviewSeek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPreviewSeek || PreviewSeekSlider == null) return;
        _playhead = SnapToFrame(Math.Clamp(PreviewSeekSlider.Value, 0, Extent));
        _last = DateTime.UtcNow;
        ShowFrame(_playing);
        OnPlayheadMoved();
    }

    void SeekTo(double time)
    {
        _playhead = SnapToFrame(Math.Clamp(time, 0, Extent));
        _last = DateTime.UtcNow;
        ShowFrame(_playing);
        OnPlayheadMoved();
    }

    void SeekStart_Click(object sender, RoutedEventArgs e) => SeekTo(0);
    void SeekBack_Click(object sender, RoutedEventArgs e) => SeekTo(_playhead - 5);
    void SeekForward_Click(object sender, RoutedEventArgs e) => SeekTo(_playhead + 5);
    void SeekFrameBack_Click(object sender, RoutedEventArgs e) => StepPlayhead(-1);
    void SeekFrameForward_Click(object sender, RoutedEventArgs e) => StepPlayhead(1);

    void FullscreenPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreenPreviewWindow != null)
        {
            _fullscreenPreviewWindow.Close();
            return;
        }
        if (PreviewHost.Parent is not Grid parent) return;
        _previewParent = parent;
        _previewParentIndex = parent.Children.IndexOf(PreviewHost);
        parent.Children.Remove(PreviewHost);
        var window = new Window
        {
            Title = "MyEditor · Full-screen preview",
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0D)),
            Owner = this,
            ShowInTaskbar = false,
            Content = PreviewHost
        };
        _fullscreenPreviewWindow = window;
        window.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { window.Close(); args.Handled = true; }
            else if (args.Key == Key.Space) { TogglePlay(); args.Handled = true; }
        };
        window.Closed += (_, _) =>
        {
            window.Content = null;
            if (_previewParent != null && ! _previewParent.Children.Contains(PreviewHost))
                _previewParent.Children.Insert(Math.Clamp(_previewParentIndex, 0, _previewParent.Children.Count), PreviewHost);
            _previewParent = null;
            _fullscreenPreviewWindow = null;
            UpdateGuide(); UpdatePreviewMouseZoom();
        };
        window.Show();
        window.Activate();
    }

    void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (Extent <= 0) { ZoomSlider.Value = 60; return; }
        DependencyObject? node = TimelineCanvas;
        while (node != null && node is not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        double available = node is ScrollViewer scroll ? scroll.ViewportWidth - TrackLabelWidth - 30 : 700;
        ZoomSlider.Value = Math.Clamp(available / Extent, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    // ---------- timeline mouse: select, seek, drag and reorder clips ----------
    object? TimelineItemAt(Point point, double time)
    {
        if (point.Y >= LaneAudioY)
            return _audios.LastOrDefault(a => a.Track == AudioTrackAt(point.Y) && time >= a.Start && time <= a.Start + a.Duration);
        if (point.Y >= LaneTextY && point.Y < LaneAudioY)
            return _overlays.LastOrDefault(o => o.IsText && o.TextTrack == TextTrackAt(point.Y) && time >= o.Start && time <= o.End);
        if (point.Y >= LaneOvY && point.Y < LaneTextY)
            return _overlays.LastOrDefault(o => !o.IsText && o.VideoTrack == VideoTrackAt(point.Y) && time >= o.Start && time <= o.End);
        if (point.Y >= LaneMainY)
            return Locate(time).clip;
        return null;
    }

    void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        bool extendSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var p = e.GetPosition(TimelineCanvas);
        if (p.X < TrackLabelWidth) return;
        double t = Math.Max(0, (p.X - TrackLabelWidth) / Pps);
        object? itemBeforeSelectionChange = TimelineItemAt(p, t);
        if (itemBeforeSelectionChange != null && IsObjectLocked(itemBeforeSelectionChange))
        {
            _playhead = SnapToFrame(Math.Clamp(t, 0, Math.Max(Extent, 0.0001)));
            _last = DateTime.UtcNow;
            ShowFrame(_playing); OnPlayheadMoved();
            Status.Text = $"{TrackKeyFor(itemBeforeSelectionChange)} is locked · click its L control to unlock";
            e.Handled = true;
            return;
        }
        bool keepGroup = !extendSelection && _multiSelection.Count > 1 && itemBeforeSelectionChange != null && _multiSelection.Contains(itemBeforeSelectionChange);
        PointerEvent? zoomHit = null;
        Clip? zoomClipHit = null;
        if (p.Y >= LaneOvY && p.Y < LaneOv3Y)
        {
            foreach (var markerEntry in _zoomMarkerOwners)
            {
                if (!_timelineBlocks.TryGetValue(markerEntry.Key, out var marker)) continue;
                double left = Canvas.GetLeft(marker), top = Canvas.GetTop(marker);
                if (p.X >= left && p.X <= left + marker.Width && p.Y >= top && p.Y <= top + marker.Height)
                { zoomHit = markerEntry.Key; zoomClipHit = markerEntry.Value; break; }
            }
        }
        BinList.SelectedItem = null;
        ClearSelection(clearMulti: !(extendSelection || keepGroup));
        _dragGroup.Clear();
        _drag = 0; _dragStarted = false;
        _dragMainClip = null; _dragOverlay = null; _dragAudio = null; _dragVisual = null; _dragZoomEvent = null;
        _marqueeSelecting = false; _selectionMarquee = null;
        bool hit = false;

        if (zoomHit != null && zoomClipHit != null)
        {
            _selected = zoomClipHit; _selectedZoomEvent = zoomHit; _zoomEventClip = zoomClipHit;
            _dragZoomEvent = zoomHit; _drag = 4; hit = true;
            double clipTimelineStart = _timeline.TakeWhile(c => c != zoomClipHit).Sum(c => c.Duration);
            _dragOrig = clipTimelineStart + (zoomHit.TimeSeconds - zoomClipHit.In) / zoomClipHit.Speed;
            _dragOrigDuration = zoomHit.ZoomDurationSeconds;
            _playhead = _dragOrig;
            if (_timelineBlocks.TryGetValue(zoomHit, out var zoomBlock) && p.X >= Canvas.GetLeft(zoomBlock) + zoomBlock.Width - 8)
                _drag = 9;
            _last = DateTime.UtcNow;
        }
        else if (p.Y >= LaneAudioY)
        {
            _activeAudioTrack = AudioTrackAt(p.Y);
            var au = _audios.LastOrDefault(q => q.Track == AudioTrackAt(p.Y) && t >= q.Start && t <= q.Start + q.Duration);
            if (au != null)
            {
                _selAu = au; _dragAudio = au; _drag = 2; _dragOrig = au.Start;
                _dragTargetTrack = au.Track;
                _dragOrigDuration = au.Duration; _dragOrigIn = au.In;
                double left = TrackLabelWidth + au.Start * Pps, right = left + au.Duration * Pps;
                if (Math.Min(Math.Abs(p.X - left), Math.Abs(p.X - right)) <= 9)
                    _drag = Math.Abs(p.X - left) < Math.Abs(p.X - right) ? 5 : 6;
                hit = true;
            }
        }
        else if (p.Y >= LaneTextY && p.Y < LaneAudioY)
        {
            var textTrack = TextTrackAt(p.Y);
            var ov = _overlays.LastOrDefault(q => q.IsText && q.TextTrack == textTrack && t >= q.Start && t <= q.End);
            if (ov != null)
            {
                _selOv = ov; _dragOverlay = ov; _drag = 1; _dragOrig = ov.Start;
                _dragTargetTextTrack = ov.TextTrack;
                _dragOrigDuration = ov.Duration; _dragOrigIn = ov.In;
                double left = TrackLabelWidth + ov.Start * Pps, right = left + ov.Duration * Pps;
                if (Math.Min(Math.Abs(p.X - left), Math.Abs(p.X - right)) <= 9)
                    _drag = Math.Abs(p.X - left) < Math.Abs(p.X - right) ? 5 : 6;
                hit = true;
            }
        }
        else if (p.Y >= LaneOvY && p.Y < LaneTextY)
        {
            int track = VideoTrackAt(p.Y);
            var ov = _overlays.LastOrDefault(q => !q.IsText && q.VideoTrack == track && t >= q.Start && t <= q.End);
            if (ov != null)
            {
                _selOv = ov; _dragOverlay = ov; _drag = 1; _dragOrig = ov.Start; _dragTargetTrack = track;
                _dragOrigDuration = ov.Duration; _dragOrigIn = ov.In;
                double left = TrackLabelWidth + ov.Start * Pps, right = left + ov.Duration * Pps;
                if (Math.Min(Math.Abs(p.X - left), Math.Abs(p.X - right)) <= 9)
                    _drag = Math.Abs(p.X - left) < Math.Abs(p.X - right) ? 5 : 6;
                hit = true;
            }
        }
        else if (p.Y >= LaneMainY)
        {
            var clip = Locate(t).clip;
            if (clip != null)
            {
                _selected = clip;
                _dragMainClip = clip;
                _drag = 3;
                _dragOrig = _timeline.TakeWhile(c => c != clip).Sum(c => c.Duration);
                _dragOrigDuration = clip.Duration; _dragOrigIn = clip.In;
                double left = TrackLabelWidth + _dragOrig * Pps;
                double right = TrackLabelWidth + (_dragOrig + clip.Duration) * Pps;
                if (!clip.IsGap && Math.Abs(p.X - left) <= 9) _drag = 5;
                else if (Math.Abs(p.X - right) <= 9) _drag = 6;
                hit = true;
            }
        }

        bool itemUnderPointer = hit;
        if (extendSelection)
        {
            object? clicked = (object?)_selAu ?? (object?)_selOv ?? _selected;
            if (clicked != null)
            {
                if (!_multiSelection.Add(clicked))
                {
                    _multiSelection.Remove(clicked);
                    if (_multiSelection.Count == 0) ClearSelection(clearMulti: false);
                    else FocusTimelineItem(_multiSelection.First());
                    hit = false;
                }
                else Status.Text = $"{_multiSelection.Count} clips selected · drag to move together, Delete to remove";
            }
        }

        if (hit)
        {
            _dragX = p.X; _dragY = p.Y;
        }
        else
        {
            _playhead = SnapToFrame(Math.Clamp(t, 0, Math.Max(Extent, 0.0001)));
            _selected = Locate(_playhead).clip;
            _last = DateTime.UtcNow;
            if (!itemUnderPointer && p.Y >= LaneMainY)
            {
                _drag = 7; _dragX = p.X; _dragY = p.Y;
                TimelineCanvas.CaptureMouse();
            }
        }

        Refresh();
        LoadInspector();
        if (hit)
        {
            object dragged = (object?)_dragMainClip ?? _dragOverlay ?? (object?)_dragAudio ?? (object?)_dragZoomEvent!;
            _timelineBlocks.TryGetValue(dragged, out _dragVisual);
            if (_dragVisual != null)
            {
                _dragOrigRight = Canvas.GetLeft(_dragVisual) + _dragVisual.Width;
                _dragOrigVisualWidth = _dragVisual.Width;
            _dragGrabOffset = _drag == 6 || _drag == 9 ? p.X - _dragOrigRight : p.X - Canvas.GetLeft(_dragVisual);
                _dragVisual.Opacity = 0.86;
                TimelineCanvas.CaptureMouse();
                bool mainAndLayers = _multiSelection.All(item => item is Clip || item is OverlayClip || item is AudioClip) && _multiSelection.Any(item => item is Clip);
                bool canMoveAsGroup = mainAndLayers && _drag == 3 ||
                                      _multiSelection.All(item => item is OverlayClip || item is AudioClip) && (_drag is 1 or 2);
                if (_multiSelection.Count > 1 && _multiSelection.Contains(dragged) && canMoveAsGroup)
                {
                    foreach (var item in _multiSelection)
                    {
                        if (!_timelineBlocks.TryGetValue(item, out var block)) continue;
                        double start = item switch
                        {
                            Clip c => _timeline.TakeWhile(q => q != c).Sum(q => q.Duration),
                            OverlayClip o => o.Start,
                            AudioClip a => a.Start,
                            _ => 0
                        };
                        _dragGroup.Add((item, start, Canvas.GetLeft(block), Canvas.GetTop(block)));
                    }
                }
            }
            else { _drag = 0; hit = false; }
        }
        if (!hit) ShowFrame(_playing);
        OnPlayheadMoved();
    }

    void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == 7 && e.LeftButton == MouseButtonState.Pressed)
        {
            Point point = e.GetPosition(TimelineCanvas);
            if (!_marqueeSelecting && Math.Abs(point.X - _dragX) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _dragY) < SystemParameters.MinimumVerticalDragDistance) return;
            _marqueeSelecting = true;
            double marqueeLeft = Math.Max(TrackLabelWidth, Math.Min(_dragX, point.X));
            double top = Math.Min(_dragY, point.Y);
            double marqueeWidth = Math.Max(1, Math.Abs(point.X - _dragX));
            double height = Math.Max(1, Math.Abs(point.Y - _dragY));
            _selectionMarquee ??= new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(48, 0x48, 0xC8, 0xE6)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x48, 0xC8, 0xE6)),
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 },
                IsHitTestVisible = false
            };
            if (!TimelineCanvas.Children.Contains(_selectionMarquee)) TimelineCanvas.Children.Add(_selectionMarquee);
            Canvas.SetLeft(_selectionMarquee, marqueeLeft); Canvas.SetTop(_selectionMarquee, top);
            _selectionMarquee.Width = marqueeWidth; _selectionMarquee.Height = height;
            return;
        }
        if (_drag == 0 || _dragVisual == null || e.LeftButton != MouseButtonState.Pressed) return;
        var viewPoint = e.GetPosition(TimelineScroll);
        if (viewPoint.X < 24 && TimelineScroll.HorizontalOffset > 0)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, TimelineScroll.HorizontalOffset - 22));
        else if (viewPoint.X > TimelineScroll.ViewportWidth - 24)
            TimelineScroll.ScrollToHorizontalOffset(Math.Min(TimelineScroll.ScrollableWidth, TimelineScroll.HorizontalOffset + 22));

        double pointerX = e.GetPosition(TimelineCanvas).X;
        double dx = pointerX - _dragX;
        if (!_dragStarted)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(e.GetPosition(TimelineCanvas).Y - _dragY) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragStarted = true;
            TimelineCanvas.Cursor = _drag is 5 or 6 or 9 ? Cursors.SizeWE : Cursors.SizeAll;
        }
        double left = Math.Max(TrackLabelWidth, pointerX - _dragGrabOffset);
        double width = _dragVisual.Width;
        if (_drag == 5)
        {
            double minimumTrimStart = _dragOrig;
            if (_dragMainClip != null)
            {
                int clipIndex = _timeline.IndexOf(_dragMainClip);
                var precedingGap = clipIndex > 0 && _timeline[clipIndex - 1].IsGap ? _timeline[clipIndex - 1] : null;
                double reclaimable = Math.Min(_dragOrigIn / _dragMainClip.Speed, precedingGap?.Duration ?? 0);
                minimumTrimStart = _dragOrig - reclaimable;
            }
            double minLeft = TrackLabelWidth + Math.Max(0, minimumTrimStart) * Pps;
            double maxLeft = _dragOrigRight - Math.Max(8, 0.1 * Pps);
            left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
            width = _dragOrigRight - left;
            _dragTargetTime = SnapToFrame((left - TrackLabelWidth) / Pps);
        }
        else if (_drag == 6)
        {
            left = _dragOrigRight - _dragOrigVisualWidth;
            double maxDuration = _dragAudio != null
                ? Math.Max(0.1, _dragAudio.SourceDuration - _dragOrigIn)
                : _dragMainClip is { IsGap: true }
                    ? 120
                : _dragOverlay != null && !_dragOverlay.IsText && !_dragOverlay.IsImage
                    ? Math.Max(0.1, _dragOverlay.SourceDuration - _dragOrigIn)
                    : _dragMainClip != null
                        ? Math.Max(0.1, (_dragMainClip.SourceDuration - _dragOrigIn) / _dragMainClip.Speed)
                        : 120;
            double minRight = left + Math.Max(8, 0.1 * Pps);
            double maxRight = left + maxDuration * Pps;
            double right = Math.Clamp(pointerX - _dragGrabOffset, minRight, Math.Max(minRight, maxRight));
            width = right - left;
            _dragTargetTime = SnapToFrame((right - TrackLabelWidth) / Pps);
        }
        else if (_drag == 9 && _dragZoomEvent != null)
        {
            left = TrackLabelWidth + _dragOrig * Pps;
            double minRight = left + Math.Max(8, 0.2 * Pps);
            double maxRight = left + 4 * Pps;
            double right = Math.Clamp(pointerX - _dragGrabOffset, minRight, maxRight);
            width = right - left;
            _dragTargetTime = SnapToFrame(width / Pps);
        }
        else
        {
            double rawStart = SnapToFrame(Math.Max(0, (left - TrackLabelWidth) / Pps));
            double duration = width / Pps;
            double snappedStart = _drag == 4 ? rawStart : SnapToFrame(SnapClipStart(rawStart, duration, (object?)_dragOverlay ?? (object?)_dragAudio ?? _dragMainClip));
            left = TrackLabelWidth + snappedStart * Pps;
            _dragTargetTime = snappedStart;
        }
        Canvas.SetLeft(_dragVisual, left);
        _dragVisual.Width = width;
        if (_dragGroup.Count <= 1 && _drag == 1 && _dragOverlay is { IsText: false })
        {
            double y = e.GetPosition(TimelineCanvas).Y;
            if (y >= LaneOvY && y < LaneTextY)
            {
                _dragTargetTrack = VideoTrackAt(y);
                Canvas.SetTop(_dragVisual, VideoLaneY(_dragTargetTrack) + 2);
            }
        }
        if (_dragGroup.Count <= 1 && _drag == 1 && _dragOverlay is { IsText: true })
        {
            double y = e.GetPosition(TimelineCanvas).Y;
            if (y >= LaneTextY && y < LaneAudioY)
            {
                _dragTargetTextTrack = TextTrackAt(y);
                Canvas.SetTop(_dragVisual, TextLaneY(_dragTargetTextTrack) + 2);
            }
        }
        if (_dragGroup.Count <= 1 && _drag == 2 && _dragAudio != null)
        {
            double y = e.GetPosition(TimelineCanvas).Y;
            if (y >= LaneAudioY && y < AudioLaneTop(_audioLaneCount) + LaneAudioH)
            {
                _dragTargetTrack = AudioTrackAt(y);
                Canvas.SetTop(_dragVisual, AudioLaneTop(_dragTargetTrack) + 1);
            }
        }

        if (_dragGroup.Count > 1 && _drag != 3)
        {
            double delta = Math.Max(-_dragGroup.Min(entry => entry.Start), _dragTargetTime - _dragOrig);
            foreach (var entry in _dragGroup)
            {
                if (!_timelineBlocks.TryGetValue(entry.Item, out var block)) continue;
                Canvas.SetLeft(block, TrackLabelWidth + Math.Max(0, entry.Start + delta) * Pps);
                Canvas.SetTop(block, entry.Top);
            }
        }
        else if (_dragGroup.Count > 1 && _drag == 3)
        {
            double delta = _dragTargetTime - _dragOrig;
            foreach (var entry in _dragGroup)
            {
                if (!_timelineBlocks.TryGetValue(entry.Item, out var block)) continue;
                Canvas.SetLeft(block, Math.Max(TrackLabelWidth, entry.Left + delta * Pps));
                Canvas.SetTop(block, entry.Top);
            }
        }

        if (_drag == 3 && _dragMainClip != null)
        {
            _dropIndicator ??= new Line { Y1 = LaneMainY - 2, Y2 = LaneMainY + LaneMainH + 2, Stroke = new SolidColorBrush(Color.FromRgb(0x52, 0xD8, 0xE8)), StrokeThickness = 3, IsHitTestVisible = false };
            if (!TimelineCanvas.Children.Contains(_dropIndicator)) TimelineCanvas.Children.Add(_dropIndicator);
            double groupDuration = _dragGroup.Count > 1 ? _dragGroup.Sum(entry => entry.Item is Clip c ? c.Duration : 0) : _dragMainClip.Duration;
            int insert = MainDropIndex(_dragTargetTime + groupDuration / 2, _dragMainClip);
            double insertX = TrackLabelWidth + _timeline.Where(c => c != _dragMainClip).Take(insert).Sum(c => c.Duration) * Pps;
            _dropIndicator.X1 = _dropIndicator.X2 = insertX;
        }
    }

    void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == 7)
        {
            bool selectedByBox = _marqueeSelecting;
            if (_selectionMarquee != null) TimelineCanvas.Children.Remove(_selectionMarquee);
            if (selectedByBox)
            {
                var a = e.GetPosition(TimelineCanvas);
                double left = Math.Max(TrackLabelWidth, Math.Min(_dragX, a.X));
                double top = Math.Min(_dragY, a.Y), right = Math.Max(_dragX, a.X), bottom = Math.Max(_dragY, a.Y);
                var box = new Rect(new Point(left, top), new Point(Math.Max(left + 1, right), Math.Max(top + 1, bottom)));
                foreach (var pair in _timelineBlocks)
                {
                    var itemType = pair.Key.GetType();
                    if (itemType != typeof(Clip) && itemType != typeof(OverlayClip) && itemType != typeof(AudioClip)) continue;
                    var block = pair.Value;
                    var bounds = new Rect(new Point(Canvas.GetLeft(block), Canvas.GetTop(block)), new Size(block.Width, block.Height));
                    if (box.IntersectsWith(bounds)) _multiSelection.Add(pair.Key);
                }
                if (_multiSelection.Count > 0)
                {
                    FocusTimelineItem(_multiSelection.First());
                    Status.Text = $"{_multiSelection.Count} clips selected · drag to move compatible selections, Delete to remove";
                }
                else
                {
                    ClearSelection();
                    Status.Text = "No clips inside selection";
                }
                Refresh(); LoadInspector();
            }
            else
            {
                ShowFrame(_playing);
                OnPlayheadMoved();
            }
            FinishTimelineDrag();
            return;
        }
        if (_drag == 0) return;
        bool moved = _dragStarted;
        if (_dragStarted && _dragGroup.Count > 1)
        {
            if (_dragGroup.All(entry => entry.Item is OverlayClip or AudioClip))
            {
                double delta = Math.Max(-_dragGroup.Min(entry => entry.Start), _dragTargetTime - _dragOrig);
                if (Math.Abs(delta) > 0.005)
                {
                    Checkpoint();
                    foreach (var entry in _dragGroup)
                    {
                        if (entry.Item is OverlayClip overlay) overlay.Start = Math.Max(0, entry.Start + delta);
                        else if (entry.Item is AudioClip audio) audio.Start = Math.Max(0, entry.Start + delta);
                    }
                    AfterEdit();
                    Status.Text = $"Moved {_dragGroup.Count} clips together";
                }
            }
            else if (_dragGroup.Any(entry => entry.Item is Clip) && _dragMainClip != null)
            {
                var originals = _timeline.Where(c => _multiSelection.Contains(c)).ToList();
                if (originals.Count > 0)
                {
                    double oldStart = _timeline.TakeWhile(c => c != originals[0]).Sum(c => c.Duration);
                    double duration = originals.Sum(c => c.Duration);
                    int rawIndex = MainDropIndex(_dragTargetTime + duration / 2, null);
                    int before = originals.Count(c => _timeline.IndexOf(c) < rawIndex);
                    int target = Math.Clamp(rawIndex - before, 0, _timeline.Count - originals.Count);
                    var reordered = _timeline.Where(c => !_multiSelection.Contains(c)).ToList();
                    reordered.InsertRange(target, originals);
                    double newStart = reordered.TakeWhile(c => c != originals[0]).Sum(c => c.Duration);
                    double shift = newStart - oldStart;
                    bool movedLayers = _multiSelection.Any(item => item is OverlayClip or AudioClip) && Math.Abs(shift) > 0.005;
                    if (!_timeline.SequenceEqual(reordered) || movedLayers)
                    {
                        Checkpoint();
                        _timeline = reordered;
                        foreach (var overlay in _multiSelection.OfType<OverlayClip>()) overlay.Start = Math.Max(0, overlay.Start + shift);
                        foreach (var audio in _multiSelection.OfType<AudioClip>()) audio.Start = Math.Max(0, audio.Start + shift);
                        AfterEdit();
                        Status.Text = $"Moved { _dragGroup.Count } selected clips together across tracks";
                    }
                }
            }
            FinishTimelineDrag();
            Refresh();
            return;
        }
        if (_dragStarted)
        {
            double newStart = Math.Max(0, _dragTargetTime);
            if (_drag == 5 && _dragOverlay != null)
            {
                double delta = newStart - _dragOrig;
                double newDuration = Math.Max(0.1, _dragOrigDuration - delta);
                Checkpoint();
                _dragOverlay.Start = _dragOrig + delta;
                _dragOverlay.Duration = newDuration;
                if (!_dragOverlay.IsText && !_dragOverlay.IsImage) _dragOverlay.In = Math.Max(0, _dragOrigIn + delta);
                AfterEdit();
            }
            else if (_drag == 5 && _dragAudio != null)
            {
                double delta = newStart - _dragOrig;
                double newIn = Math.Max(0, _dragOrigIn + delta);
                double newDuration = Math.Max(0.1, _dragOrigDuration - (newIn - _dragOrigIn));
                Checkpoint(); _dragAudio.Start = _dragOrig + (newIn - _dragOrigIn); _dragAudio.In = newIn; _dragAudio.Duration = newDuration; AfterEdit();
            }
            else if (_drag == 6 && _dragOverlay != null)
            {
                double duration = Math.Max(0.1, _dragTargetTime - _dragOrig);
                if (!_dragOverlay.IsText && !_dragOverlay.IsImage) duration = Math.Min(duration, Math.Max(0.1, _dragOverlay.SourceDuration - _dragOrigIn));
                Checkpoint(); _dragOverlay.Duration = duration; AfterEdit();
            }
            else if (_drag == 6 && _dragAudio != null)
            {
                double duration = Math.Clamp(_dragTargetTime - _dragOrig, 0.1, Math.Max(0.1, _dragAudio.SourceDuration - _dragOrigIn));
                Checkpoint(); _dragAudio.Duration = duration; AfterEdit();
            }
            else if (_drag == 5 && _dragMainClip is { IsGap: false } mainClip)
            {
                int index = _timeline.IndexOf(mainClip);
                var precedingGap = index > 0 && _timeline[index - 1].IsGap ? _timeline[index - 1] : null;
                double minDelta = -Math.Min(_dragOrigIn / mainClip.Speed, precedingGap?.Duration ?? 0);
                double maxDelta = Math.Max(0, (mainClip.Out - _dragOrigIn - 0.1) / mainClip.Speed);
                double delta = Math.Clamp(_dragTargetTime - _dragOrig, minDelta, maxDelta);
                double newIn = Math.Clamp(_dragOrigIn + delta * mainClip.Speed, 0, mainClip.Out - 0.1);
                double appliedDelta = (newIn - _dragOrigIn) / mainClip.Speed;
                if (Math.Abs(appliedDelta) > 0.005)
                {
                    Checkpoint();
                    if (precedingGap != null)
                    {
                        double remaining = precedingGap.Duration + appliedDelta;
                        if (remaining < 0.1) _timeline.Remove(precedingGap);
                        else precedingGap.GapDuration = remaining;
                    }
                    else if (appliedDelta > 0.005)
                        _timeline.Insert(index, new Clip { IsGap = true, GapDuration = appliedDelta });
                    mainClip.In = newIn;
                    AfterEdit();
                }
            }
            else if (_drag == 6 && _dragMainClip != null)
            {
                double duration = Math.Clamp(_dragTargetTime - _dragOrig, 0.1,
                    _dragMainClip.IsGap ? 120 : Math.Max(0.1, (_dragMainClip.SourceDuration - _dragOrigIn) / _dragMainClip.Speed));
                Checkpoint();
                if (_dragMainClip.IsGap) ResizeGap(_dragMainClip, duration);
                else _dragMainClip.Out = _dragMainClip.In + duration * _dragMainClip.Speed;
                AfterEdit();
            }
            else if (_drag == 1 && _dragOverlay != null &&
                (Math.Abs(newStart - _dragOverlay.Start) > 0.005 ||
                 (_dragOverlay.IsText ? _dragTargetTextTrack != _dragOverlay.TextTrack : _dragTargetTrack != _dragOverlay.VideoTrack)))
            {
                Checkpoint(); _dragOverlay.Start = newStart;
                if (_dragOverlay.IsText) _dragOverlay.TextTrack = _dragTargetTextTrack;
                else _dragOverlay.VideoTrack = _dragTargetTrack;
                AfterEdit();
            }
            else if (_drag == 2 && _dragAudio != null &&
                (Math.Abs(newStart - _dragAudio.Start) > 0.005 || _dragTargetTrack != _dragAudio.Track))
            {
                Checkpoint(); _dragAudio.Start = newStart; _dragAudio.Track = _dragTargetTrack; AfterEdit();
            }
            else if (_drag == 9 && _dragZoomEvent != null)
            {
                double duration = Math.Clamp(_dragTargetTime, 0.2, 4);
                if (Math.Abs(duration - _dragOrigDuration) > 0.01)
                {
                    Checkpoint();
                    _dragZoomEvent.ZoomDurationSeconds = duration;
                    _dragZoomEvent.ZoomSpeedSeconds = Math.Min(_dragZoomEvent.ZoomSpeedSeconds, duration / 2);
                    AfterEdit();
                }
            }
            else if (_drag == 4 && _dragZoomEvent != null && _zoomEventClip != null)
            {
                double videoStart = _timeline.TakeWhile(c => c != _zoomEventClip).Sum(c => c.Duration);
                Checkpoint();
                _dragZoomEvent.TimeSeconds = _zoomEventClip.In + Math.Clamp(_dragTargetTime - videoStart, 0, _zoomEventClip.Duration) * _zoomEventClip.Speed;
                _zoomEventClip.PointerEvents = _zoomEventClip.PointerEvents.OrderBy(e => e.TimeSeconds).ToList();
                AfterEdit();
            }
            else if (_drag == 3 && _dragMainClip != null)
            {
                int from = _timeline.IndexOf(_dragMainClip);
                int to = MainDropIndex(_dragTargetTime + _dragMainClip.Duration / 2, _dragMainClip);
                if (from >= 0 && to != from)
                {
                    Checkpoint(); _timeline.RemoveAt(from);
                    _timeline.Insert(Math.Clamp(to, 0, _timeline.Count), _dragMainClip);
                    AfterEdit();
                }
            }
        }
        FinishTimelineDrag();
        if (moved) Refresh();
    }

    int MainDropIndex(double centerTime, Clip? dragged)
    {
        int index = 0;
        double cursor = 0;
        foreach (var clip in _timeline)
        {
            if (clip == dragged) continue;
            if (centerTime > cursor + clip.Duration / 2) index++;
            cursor += clip.Duration;
        }
        return index;
    }

    double SnapClipStart(double proposedStart, double clipDuration, object? dragged)
    {
        if (SnapCheck?.IsChecked != true) return proposedStart;
        double nearest = proposedStart;
        double distance = 10 / Math.Max(1, Pps);
        void Check(double candidate)
        {
            double d = Math.Abs(candidate - proposedStart);
            if (d <= distance) { distance = d; nearest = candidate; }
        }

        Check(0);
        Check(_playhead);
        double cursor = 0;
        foreach (var clip in _timeline)
        {
            if (!ReferenceEquals(clip, dragged)) { Check(cursor); Check(cursor - clipDuration); Check(cursor + clip.Duration); Check(cursor + clip.Duration - clipDuration); }
            cursor += clip.Duration;
        }
        foreach (var overlay in _overlays)
            if (!ReferenceEquals(overlay, dragged)) { Check(overlay.Start); Check(overlay.End); Check(overlay.Start - clipDuration); Check(overlay.End - clipDuration); }
        foreach (var audio in _audios)
            if (!ReferenceEquals(audio, dragged)) { Check(audio.Start); Check(audio.Start + audio.Duration); Check(audio.Start - clipDuration); Check(audio.Start + audio.Duration - clipDuration); }
        return nearest;
    }

    void FinishTimelineDrag()
    {
        if (_dragVisual != null) _dragVisual.Opacity = 1;
        if (_selectionMarquee != null) TimelineCanvas.Children.Remove(_selectionMarquee);
        _selectionMarquee = null; _marqueeSelecting = false;
        TimelineCanvas.Cursor = Cursors.Arrow;
        _drag = 0; _dragStarted = false;
        _dragMainClip = null; _dragOverlay = null; _dragAudio = null; _dragVisual = null; _dragZoomEvent = null; _dropIndicator = null;
        _dragGroup.Clear();
        if (TimelineCanvas.IsMouseCaptured) TimelineCanvas.ReleaseMouseCapture();
    }

    void Timeline_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_drag == 0) return;
        _dragVisual?.SetCurrentValue(UIElement.OpacityProperty, 1.0);
        if (_dropIndicator != null) TimelineCanvas.Children.Remove(_dropIndicator);
        FinishTimelineDrag();
        Refresh();
    }

    CancellationTokenSource? _exportCts;

    static string FirstLine(string text)
    {
        var line = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return line ?? "";
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        // While an export is running this same button becomes "Cancel export".
        if (_exportCts != null) { _exportCts.Cancel(); Status.Text = "Cancelling export…"; return; }

        if (_timeline.Count == 0) { MessageBox.Show("The main track (V1) is empty. Add a video or image first."); return; }
        Pause();
        if (!FFmpegService.ToolsInstalled && !await EnsureMediaToolsInteractive()) return;
        var dlg = new SaveFileDialog { Filter = "MP4 video|*.mp4", FileName = "output.mp4", DefaultExt = ".mp4", AddExtension = true };
        if (dlg.ShowDialog() != true) return;

        // Never let FFmpeg overwrite one of the project's own source files.
        string outFull = System.IO.Path.GetFullPath(dlg.FileName);
        bool overwritesSource = _timeline.Select(c => c.Path)
            .Concat(_overlays.Where(o => !o.IsText).Select(o => o.Path))
            .Concat(_audios.Select(a => a.Path))
            .Append(_backgroundImagePath)
            .Any(p => !string.IsNullOrEmpty(p) &&
                      string.Equals(System.IO.Path.GetFullPath(p), outFull, StringComparison.OrdinalIgnoreCase));
        if (overwritesSource)
        {
            MessageBox.Show(this, "That file name is the same as a source file used in this project, and exporting would overwrite your original media.\n\nChoose a different file name.",
                "Choose another file name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var missingSources = _timeline.Where(c => !c.IsGap).Select(c => c.Path)
            .Concat(_overlays.Where(o => !o.IsText).Select(o => o.Path))
            .Concat(_audios.Select(a => a.Path))
            .Where(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        if (missingSources.Count > 0)
        {
            string list = string.Join("\n", missingSources.Select(p => "• " +
                (string.IsNullOrWhiteSpace(p) ? "Media path unavailable" : System.IO.Path.GetFileName(p))));
            MessageBox.Show(this,
                "Export can't start because some media files used by the timeline are missing. Reconnect or remove these clips, then export again:\n\n" + list,
                "Media files missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profile = Presets[Math.Clamp(QualityBox.SelectedIndex, 0, Presets.Length - 1)];
        int aspect = Math.Clamp(AspectBox.SelectedIndex, 0, 2);
        int shortEdge = profile.Height;
        var settings = aspect switch
        {
            1 => new ExportSettings(profile.Label + " Portrait", shortEdge, shortEdge * 16 / 9, profile.Fps, true, profile.LowEndCpu),
            2 => new ExportSettings(profile.Label + " Square", shortEdge, shortEdge, profile.Fps, true, profile.LowEndCpu),
            _ => profile
        };
        settings = settings with
        {
            Encoder = SelectedEncoder, VideoQuality = ComboVal(VideoQualityBox), AudioBitrateKbps = SelectedAudioKbps,
            BackgroundMode = _backgroundMode, BackgroundColor = _backgroundColor,
            BackgroundImagePath = _backgroundImagePath
        };
        var exportMain = CloneList(_timeline);
        if (IsTrackMuted("V1")) foreach (var clip in exportMain) { clip.Volume = 0; clip.Keys.Remove(ClipMotion.Volume); }
        var exportOverlays = _overlays.Select(o => o.Clone()).ToList();
        foreach (var overlay in exportOverlays.Where(o => IsTrackMuted(TrackKeyFor(o))))
        {
            if (overlay.IsText) overlay.TextOpacity = 0;
            else overlay.Opacity = 0;
        }
        var exportAudio = _audios.Select(a => a.Clone()).ToList();
        foreach (var audio in exportAudio.Where(a => IsTrackMuted($"A{a.Track}"))) audio.Volume = 0;

        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        bool cancelled = false;
        var startedAt = DateTime.UtcNow;
        // Render beside the destination, then publish it only after FFmpeg has closed
        // and finalized the MP4. A browser opening the destination during export would
        // otherwise see an unfinished file with no readable duration/index yet.
        string exportPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(outFull)!,
            $".{System.IO.Path.GetFileNameWithoutExtension(outFull)}.{Guid.NewGuid():N}.partial.mp4");
        ExportBar.Value = 0; ExportBar.Visibility = Visibility.Visible;
        ExportBtn.Content = "Cancel export";
        try
        {
            var prog = new Progress<double>(p =>
            {
                if (p >= 1)
                {
                    ExportBar.IsIndeterminate = false;
                    ExportBar.Value = 100;
                    Status.Text = "Export complete";
                    return;
                }
                p = Math.Clamp(p, 0, 0.99);
                if (p >= 0.99)
                {
                    // Encoding has reached the end of the timeline, but FFmpeg may still
                    // be writing the MP4 index (especially with +faststart). Don't leave
                    // a nearly full bar looking frozen; show activity and elapsed time.
                    ExportBar.IsIndeterminate = true;
                    Status.Text = $"Finalizing MP4… {Fmt((DateTime.UtcNow - startedAt).TotalSeconds)} elapsed · Cancel export available";
                    return;
                }
                ExportBar.IsIndeterminate = false;
                ExportBar.Value = p * 100;
                double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
                string left = p > 0.03 && elapsed > 3 ? $" · about {Fmt(elapsed * (1 - p) / p)} left" : "";
                Status.Text = $"Exporting {p:P0}{left}";
            });
            try
            {
                await FFmpegService.ExportAsync(exportMain, exportOverlays, exportAudio, exportPath, settings, prog, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && FFmpegService.CanRetryWithCpu(settings.Encoder))
            {
                var retry = MessageBox.Show(this,
                    "The selected GPU or Windows media encoder could not run on this PC. Retry with the standard CPU encoder (H.264)?\n\nDetails: " + FirstLine(ex.Message),
                    "Encoder unavailable", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (retry != MessageBoxResult.Yes) throw;
                settings = settings with { Encoder = "h264" };
                ExportBar.Value = 0; startedAt = DateTime.UtcNow;
                await FFmpegService.ExportAsync(exportMain, exportOverlays, exportAudio, exportPath, settings, prog, token);
            }
            var rendered = new System.IO.FileInfo(exportPath);
            if (!rendered.Exists || rendered.Length == 0)
                throw new InvalidOperationException("FFmpeg finished without creating a playable MP4 file.");
            var renderedMedia = await FFmpegService.ProbeAsync(exportPath);
            if (!renderedMedia.HasVideo || renderedMedia.Width <= 0 || renderedMedia.Height <= 0)
                throw new InvalidDataException("The export file did not contain a valid video stream, so it was not marked complete.");
            System.IO.File.Move(exportPath, outFull, overwrite: true);
            var open = MessageBox.Show(this, "Export complete!\n\nShow the file in its folder?", "Export",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outFull}\""); } catch { }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed"); }
        finally
        {
            try { if (System.IO.File.Exists(exportPath)) System.IO.File.Delete(exportPath); } catch { }
            _exportCts?.Dispose(); _exportCts = null;
            ExportBar.IsIndeterminate = false;
            ExportBar.Visibility = Visibility.Collapsed;
            ExportBtn.Content = "Export  ⤴";
            Status.Text = cancelled ? "Export cancelled" : "Ready";
        }
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;   // don't trigger shortcuts while typing
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.N when ctrl: NewProject_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O when ctrl: OpenProject_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S when ctrlShift: SaveProjectAsDialog(); e.Handled = true; break;
            case Key.S when ctrl: SaveProjectDialog(); e.Handled = true; break;
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None: Split(); e.Handled = true; break;
            case Key.Delete when Keyboard.Modifiers == ModifierKeys.Shift: RippleDeleteSelectedMainClip(); e.Handled = true; break;
            case Key.Delete: DeleteCurrentSelection(); e.Handled = true; break;
            case Key.Z when ctrl: Undo(); e.Handled = true; break;
            case Key.Y when ctrl: Redo(); e.Handled = true; break;
            case Key.D when ctrl: DuplicateSelected(); e.Handled = true; break;
            case Key.E when ctrl: Export_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.A when ctrl: SelectAllTimelineItems(); e.Handled = true; break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.None: StepPlayhead(-1); e.Handled = true; break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.None: StepPlayhead(1); e.Handled = true; break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Shift: StepPlayhead(-1, seconds: true); e.Handled = true; break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Shift: StepPlayhead(1, seconds: true); e.Handled = true; break;
            case Key.Home: if (_playing) Pause(); SeekTo(0); e.Handled = true; break;
            case Key.End: if (_playing) Pause(); SeekTo(Extent); e.Handled = true; break;
        }
    }

    /// <summary>Arrow keys: one frame at the chosen export frame rate; with Shift, one second.</summary>
    void StepPlayhead(int direction, bool seconds = false)
    {
        if (_playing) Pause();
        SeekTo(_playhead + direction * (seconds ? 1.0 : 1.0 / TimelineFps));
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr window, int id);
}
