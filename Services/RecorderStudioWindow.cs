using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VideoEditor.Services;

/// <summary>Recorder Studio: quality, encoder, audio mix, webcam bubble, cursor and safety options.</summary>
public sealed class RecorderStudioWindow : Window
{
    static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x33));
    static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF2));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA8, 0xAD, 0xBA));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x95));
    const string Off = "Off";

    readonly ComboBox _resolution = new(), _fps = new(), _quality = new(), _encoder = new();
    readonly ComboBox _systemAudio = new(), _webcam = new(), _corner = new(), _countdown = new(), _limit = new();
    readonly Slider _camSize = new() { Minimum = 10, Maximum = 35, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock _camSizeText = new() { Foreground = Muted, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _mirror = new() { Content = "Mirror webcam (like a mirror)" };
    readonly CheckBox _cursor = new() { Content = "Show mouse cursor in the recording" };
    readonly TextBlock _hardwareInfo = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) };
    readonly RecorderOptions _options;

    public RecorderOptions Result => _options;

    public RecorderStudioWindow(RecorderOptions current)
    {
        _options = current.Clone();
        Title = "MyEditor · Recorder Studio";
        Width = 560; Height = Math.Min(720, SystemParameters.WorkArea.Height * 0.92);
        MinWidth = 480; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x20));
        Foreground = TextBrush;
        WindowUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "RECORDER STUDIO", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Accent });
        header.Children.Add(new TextBlock { Text = "Hardware encoding, mic + system audio, webcam bubble and safety options. Settings are remembered.", TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = MakeButton("Cancel", 90, PanelBrush);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var apply = MakeButton("Save settings", 130, new SolidColorBrush(Color.FromRgb(0xB3, 0x32, 0x40)));
        apply.IsDefault = true; apply.FontWeight = FontWeights.SemiBold;
        apply.Click += (_, _) => { Collect(); DialogResult = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // ---- Video ----
        Section(body, "Video");
        Fill(_resolution, new[] { "Follow recording mode", "Native size", "1440p", "1080p", "720p", "540p (low load)" });
        Row(body, "Resolution (never upscales)", _resolution);
        Fill(_fps, new[] { "Follow recording mode", "15 fps", "24 fps", "30 fps", "60 fps" });
        Row(body, "Frame rate", _fps);
        Fill(_quality, new[] { "Small files", "Balanced", "High quality" });
        Row(body, "Quality", _quality);
        Fill(_encoder, new[] { "Auto (GPU/native when available)", "CPU (x264)", "NVIDIA NVENC", "Intel Quick Sync", "AMD AMF", "Windows Media Foundation (native)" });
        Row(body, "Encoder", _encoder);
        body.Children.Add(_hardwareInfo);
        _hardwareInfo.Text = "Checking which GPU encoders your FFmpeg build supports…";
        var liteAssist = MakeButton("✦  Lite Assist · check lag", 210, new SolidColorBrush(Color.FromRgb(0x3B, 0x5F, 0x91)));
        liteAssist.HorizontalAlignment = HorizontalAlignment.Left;
        liteAssist.Click += (_, _) => MessageBox.Show(this, FastDiagnosticsService.CheckPerformance(), "MyEditor · Lite Assist", MessageBoxButton.OK, MessageBoxImage.Information);
        body.Children.Add(liteAssist);
        body.Children.Add(Pad(_cursor));

        // ---- Audio ----
        Section(body, "Audio");
        body.Children.Add(new TextBlock { Text = "Choose desktop audio to capture everything played by Windows using WASAPI loopback. You can also choose a hardware loopback input such as Stereo Mix. The microphone is chosen in the main toolbar; both sources are mixed into the recording.", TextWrapping = TextWrapping.Wrap, Foreground = Muted });
        Fill(_systemAudio, new[] { Off });
        Row(body, "System / desktop audio input", _systemAudio);
        body.Children.Add(new TextBlock { Text = "WASAPI captures the default playback device directly and does not require Stereo Mix. Keep the playback device unchanged while recording.", TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11 });

        // ---- Webcam ----
        Section(body, "Webcam bubble");
        Fill(_webcam, new[] { Off });
        Row(body, "Camera", _webcam);
        Fill(_corner, new[] { "Bottom right", "Bottom left", "Top right", "Top left" });
        Row(body, "Position", _corner);
        var sizeRow = new DockPanel();
        DockPanel.SetDock(_camSizeText, Dock.Right);
        sizeRow.Children.Add(_camSizeText); sizeRow.Children.Add(_camSize);
        _camSize.ValueChanged += (_, _) => _camSizeText.Text = (int)_camSize.Value + "% of width";
        Row(body, "Size", sizeRow);
        body.Children.Add(Pad(_mirror));

        // ---- Behaviour ----
        Section(body, "Safety & behaviour");
        Fill(_countdown, new[] { "No countdown", "3 seconds", "5 seconds", "10 seconds" });
        Row(body, "Countdown before recording", _countdown);
        Fill(_limit, new[] { "No limit", "5 minutes", "15 minutes", "30 minutes", "60 minutes", "120 minutes" });
        Row(body, "Stop automatically after", _limit);
        body.Children.Add(new TextBlock { Text = "Recordings are written as crash-safe segments and joined when you stop, so a crash or power cut doesn't lose the whole take.", TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });

        ApplyStyles(body);
        Load();
        Loaded += async (_, _) => await LoadDevicesAsync();
    }

    // ------------------------------------------------------------ data <-> controls
    static readonly int[] Heights = { -1, 0, 1440, 1080, 720, 540 };
    static readonly int[] FpsValues = { 0, 15, 24, 30, 60 };
    static readonly int[] Countdowns = { 0, 3, 5, 10 };
    static readonly int[] Limits = { 0, 5, 15, 30, 60, 120 };

    void Load()
    {
        _resolution.SelectedIndex = Math.Max(0, Array.IndexOf(Heights, _options.HeightOverride));
        _fps.SelectedIndex = Math.Max(0, Array.IndexOf(FpsValues, _options.FpsOverride));
        _quality.SelectedIndex = (int)_options.Quality;
        _encoder.SelectedIndex = (int)_options.Encoder;
        _corner.SelectedIndex = (int)_options.WebcamPosition;
        _camSize.Value = Math.Clamp(_options.WebcamSizePercent, 10, 35);
        _camSizeText.Text = (int)_camSize.Value + "% of width";
        _mirror.IsChecked = _options.WebcamMirror;
        _cursor.IsChecked = _options.ShowCursor;
        _countdown.SelectedIndex = Math.Max(0, Array.IndexOf(Countdowns, _options.CountdownSeconds));
        _limit.SelectedIndex = Math.Max(0, Array.IndexOf(Limits, _options.MaxMinutes));
    }

    void Collect()
    {
        _options.HeightOverride = Heights[Math.Clamp(_resolution.SelectedIndex, 0, Heights.Length - 1)];
        _options.FpsOverride = FpsValues[Math.Clamp(_fps.SelectedIndex, 0, FpsValues.Length - 1)];
        _options.Quality = (RecorderQuality)Math.Clamp(_quality.SelectedIndex, 0, 2);
        _options.Encoder = (RecorderEncoder)Math.Clamp(_encoder.SelectedIndex, 0, 5);
        _options.SystemAudioDevice = _systemAudio.SelectedIndex == 1
            ? RecorderAudioSource.WasapiLoopback
            : _systemAudio.SelectedIndex > 1 ? _systemAudio.SelectedItem as string ?? "" : "";
        _options.WebcamDevice = _webcam.SelectedIndex > 0 ? _webcam.SelectedItem as string ?? "" : "";
        _options.WebcamPosition = (WebcamCorner)Math.Clamp(_corner.SelectedIndex, 0, 3);
        _options.WebcamSizePercent = (int)_camSize.Value;
        _options.WebcamMirror = _mirror.IsChecked == true;
        _options.ShowCursor = _cursor.IsChecked == true;
        _options.CountdownSeconds = Countdowns[Math.Clamp(_countdown.SelectedIndex, 0, Countdowns.Length - 1)];
        _options.MaxMinutes = Limits[Math.Clamp(_limit.SelectedIndex, 0, Limits.Length - 1)];
    }

    async Task LoadDevicesAsync()
    {
        try
        {
            var hardware = await ScreenRecorderService.DetectHardwareEncodersAsync();
            var names = new List<string>();
            if (hardware.Contains("h264_nvenc")) names.Add("NVIDIA NVENC");
            if (hardware.Contains("h264_qsv")) names.Add("Intel Quick Sync");
            if (hardware.Contains("h264_amf")) names.Add("AMD AMF");
            if (hardware.Contains("h264_mf")) names.Add("Windows Media Foundation");
            _hardwareInfo.Text = names.Count == 0
                ? "No GPU encoder found in this FFmpeg build. CPU x264 will be used."
                : "Available in this FFmpeg build: " + string.Join(", ", names) + ". If a GPU encoder can't start on this PC, the recorder falls back to CPU automatically.";
        }
        catch { _hardwareInfo.Text = "Could not check GPU encoders (is FFmpeg set up?). CPU x264 will be used."; }

        Fill(_systemAudio, new[] { Off, "Desktop audio · WASAPI loopback" });
        if (RecorderAudioSource.IsWasapiLoopback(_options.SystemAudioDevice)) _systemAudio.SelectedIndex = 1;
        try
        {
            var devices = await ScreenRecorderService.ListDevicesAsync();
            Fill(_systemAudio, new[] { Off, "Desktop audio · WASAPI loopback" }.Concat(devices.Audio).ToArray());
            Fill(_webcam, new[] { Off }.Concat(devices.Video).ToArray());
            SelectByName(_systemAudio, _options.SystemAudioDevice);
            if (RecorderAudioSource.IsWasapiLoopback(_options.SystemAudioDevice)) _systemAudio.SelectedIndex = 1;
            SelectByName(_webcam, _options.WebcamDevice);
            // Pre-select a loopback-style input when the user has none chosen yet.
            if (_systemAudio.SelectedIndex <= 0 && string.IsNullOrEmpty(_options.SystemAudioDevice))
            {
                int loop = devices.Audio.ToList().FindIndex(n => n.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("virtual-audio-capturer", StringComparison.OrdinalIgnoreCase) || n.Contains("loopback", StringComparison.OrdinalIgnoreCase));
                if (loop >= 0) _systemAudio.ToolTip = "A loopback-style input was found: " + devices.Audio[loop];
            }
        }
        catch
        {
            Fill(_webcam, new[] { Off });
            if (RecorderAudioSource.IsWasapiLoopback(_options.SystemAudioDevice)) _systemAudio.SelectedIndex = 1;
            /* WASAPI loopback remains available even without DirectShow enumeration. */
        }
    }

    // ------------------------------------------------------------ small UI helpers
    static void SelectByName(ComboBox box, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { box.SelectedIndex = 0; return; }
        for (int i = 0; i < box.Items.Count; i++)
            if (string.Equals(box.Items[i] as string, name, StringComparison.OrdinalIgnoreCase)) { box.SelectedIndex = i; return; }
        box.SelectedIndex = 0;
    }

    static void Fill(ComboBox box, string[] items)
    {
        box.Items.Clear();
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = 0;
    }

    static Button MakeButton(string text, double width, Brush background) => new()
    {
        Content = text, MinWidth = width, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6),
        Background = background, Foreground = Brushes.White, BorderThickness = new Thickness(0)
    };

    static FrameworkElement Pad(FrameworkElement element) { element.Margin = new Thickness(0, 8, 0, 0); return element; }

    static void Section(Panel panel, string title) => panel.Children.Add(new TextBlock
    {
        Text = title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Accent,
        Margin = new Thickness(0, 18, 0, 4)
    });

    static void Row(Panel panel, string label, UIElement control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 2), Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCB, 0xD4)) });
        panel.Children.Add(control);
    }

    static void ApplyStyles(Panel body)
    {
        var item = new Style(typeof(ComboBoxItem));
        item.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush));
        item.Setters.Add(new Setter(Control.BackgroundProperty, PanelBrush));
        item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        item.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        foreach (var child in body.Children)
        {
            if (child is ComboBox combo)
            {
                combo.Foreground = TextBrush; combo.Background = PanelBrush; combo.MaxDropDownHeight = 260;
                combo.Padding = new Thickness(8, 5, 8, 5); combo.ItemContainerStyle = item;
            }
            else if (child is CheckBox check) check.Foreground = TextBrush;
        }
    }
}

/// <summary>Big “3 · 2 · 1” overlay shown before capture starts (it is never part of the recording).</summary>
public sealed class CountdownWindow : Window
{
    readonly TextBlock _number = new()
    {
        FontSize = 120, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TaskCompletionSource _done = new();
    int _left;

    CountdownWindow(int seconds)
    {
        _left = seconds;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent; Width = 260; Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new Border
        {
            CornerRadius = new CornerRadius(130), Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x16, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0x4B, 0x58)), BorderThickness = new Thickness(5), Child = _number
        };
        MouseDown += (_, _) => Finish();                       // click to skip
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Space or Key.Enter) Finish(); };
        _number.Text = _left.ToString();
        _timer.Tick += (_, _) => { _left--; if (_left <= 0) Finish(); else _number.Text = _left.ToString(); };
        Loaded += (_, _) => { _timer.Start(); Activate(); };
    }

    void Finish()
    {
        _timer.Stop();
        _done.TrySetResult();
        Close();
    }

    /// <summary>Shows the countdown and returns when it ends (or the user clicks to skip). 0 seconds returns immediately.</summary>
    public static async Task RunAsync(int seconds)
    {
        if (seconds <= 0) return;
        var window = new CountdownWindow(seconds);
        window.Show();
        await window._done.Task;
    }
}
