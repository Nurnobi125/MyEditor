using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VideoEditor.Services;

/// <summary>Quick capture setup shown before a screen recording starts.</summary>
public sealed class RecordingSetupWindow : Window
{
    static readonly Brush Bg = Brush("#17191F"), Card = Brush("#242832"), Accent = Brush("#7C69F5"), Muted = Brush("#9DA5B3");
    readonly RecorderOptions _options;
    readonly IReadOnlyList<string> _audioDevices, _videoDevices;
    readonly Button[] _modeCards = new Button[3];
    readonly ComboBox _camera = new(), _microphone = new(), _systemAudio = new();
    readonly ComboBox _webcamLayout = new();
    readonly CheckBox _smartAssist = new() { Content = "Use Local Smart Assist · fit capture to this PC (offline, no model download)", Foreground = Brushes.White, Margin = new Thickness(0, 13, 0, 0) };
    readonly CheckBox _trackMouse = new() { Content = "Track mouse movement and clicks · enable click highlights and automatic focus zoom", Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _trackTyping = new() { Content = "Add gentle focus zoom while typing", Foreground = Brushes.White, Margin = new Thickness(22, 5, 0, 0) };
    readonly TextBlock _areaLabel = new(), _windowLabel = new();
    int _mode;
    CaptureArea? _area;
    CaptureWindowInfo? _window;

    public CaptureArea? SelectedArea => _mode == 1 ? _area : null;
    public CaptureWindowInfo? SelectedWindow => _mode == 2 ? _window : null;
    public string? SelectedCamera => _camera.SelectedIndex > 0 ? _camera.SelectedItem as string : null;
    public string? SelectedMicrophone => _microphone.SelectedIndex > 0 ? _microphone.SelectedItem as string : null;
    public string SelectedSystemAudio => _systemAudio.SelectedIndex == 1 ? RecorderAudioSource.WasapiLoopback :
        _systemAudio.SelectedIndex > 1 ? _systemAudio.SelectedItem as string ?? "" : "";
    public RecorderOptions Options => _options;

    public RecordingSetupWindow(RecorderOptions current, IReadOnlyList<string> audioDevices, IReadOnlyList<string> videoDevices,
        CaptureArea? area, CaptureWindowInfo? window, string? microphone)
    {
        _options = current.Clone(); _audioDevices = audioDevices; _videoDevices = videoDevices; _area = area; _window = window;
        _mode = window != null ? 2 : area != null ? 1 : 0;
        Title = "MyEditor · Recording setup"; Width = 820; Height = 462; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Bg; Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) };
        var fast = new TextBlock { Text = "●  Local recording · no account required", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(fast, Dock.Left); footer.Children.Add(fast);
        var start = Button("●  Start recording", Accent, 155); start.FontWeight = FontWeights.SemiBold; start.IsDefault = true;
        start.Click += (_, _) => { if (ValidSource()) { Collect(); DialogResult = true; Close(); } };
        var cancel = Button("Cancel", Card, 86); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(start, Dock.Right); DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(start); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "Set up your recording", FontSize = 23, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Choose what to capture and which devices to include.", Foreground = Muted, Margin = new Thickness(0, 4, 0, 16) });
        body.Children.Add(new TextBlock { Text = "RECORDING MODE", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) });

        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
        string[] names = { "Full screen", "Custom area", "Window" }, icons = { "▣", "⌗", "▱" };
        for (int i = 0; i < 3; i++)
        {
            int mode = i;
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = icons[i], FontSize = 28, Foreground = Brush("#B8B2FF"), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = names[i], FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            _modeCards[i] = Button("", Card, 0); _modeCards[i].Height = 100; _modeCards[i].Margin = new Thickness(0, 0, i < 2 ? 10 : 0, 0); _modeCards[i].Content = stack;
            _modeCards[i].Click += (_, _) => ChooseMode(mode);
            cards.Children.Add(_modeCards[i]);
        }
        body.Children.Add(cards);

        var sourceRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 3, 0, 15) };
        _areaLabel.Foreground = Muted; _windowLabel.Foreground = Muted;
        var chooseSource = Button("Choose source…", Card, 132); chooseSource.Click += ChooseSource_Click;
        DockPanel.SetDock(chooseSource, Dock.Left); sourceRow.Children.Add(chooseSource);
        DockPanel.SetDock(_areaLabel, Dock.Left); sourceRow.Children.Add(_areaLabel);
        DockPanel.SetDock(_windowLabel, Dock.Left); sourceRow.Children.Add(_windowLabel);
        body.Children.Add(sourceRow);

        body.Children.Add(new TextBlock { Text = "DEVICES & TOOLS", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 0, 0, 7) });
        var devices = new UniformGrid { Columns = 3 };
        AddDevice(devices, "Camera", _camera, new[] { "Off" }.Concat(videoDevices).ToArray(), _options.WebcamDevice);
        AddDevice(devices, "Microphone", _microphone, new[] { "Off" }.Concat(audioDevices).ToArray(), microphone ?? _options.MicrophoneDevice);
        AddDevice(devices, "System audio", _systemAudio, new[] { "Off", "Desktop audio · WASAPI" }.Concat(audioDevices).ToArray(), _options.SystemAudioDevice);
        body.Children.Add(devices);
        _webcamLayout.Items.Add("Picture-in-picture · webcam in a corner");
        _webcamLayout.Items.Add("Split screen · screen + webcam side by side");
        _webcamLayout.Items.Add("Presenter focus · larger webcam overlay");
        _webcamLayout.SelectedIndex = Math.Clamp((int)_options.WebcamLayout, 0, 2);
        var layoutRow = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        layoutRow.Children.Add(new TextBlock { Text = "SCREEN + WEBCAM LAYOUT", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 0, 0, 5) });
        _webcamLayout.Background = Card; _webcamLayout.Foreground = Brushes.White; _webcamLayout.Padding = new Thickness(8, 6, 8, 6);
        layoutRow.Children.Add(_webcamLayout);
        body.Children.Add(layoutRow);
        body.Children.Add(new TextBlock { Text = "Choose a camera above to include it. The screen and camera are recorded together as one synchronized video clip.", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        _trackMouse.IsChecked = _options.TrackMouse;
        _trackTyping.IsChecked = _options.TrackTyping;
        body.Children.Add(_trackMouse);
        body.Children.Add(_trackTyping);
        _smartAssist.IsChecked = _options.SmartAssistEnabled;
        body.Children.Add(_smartAssist);
        var note = new TextBlock { Text = "Cursor tracking and click/typing focus zoom are included. Use Recorder Studio for frame rate, quality, encoder, countdown, and other advanced options.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 0), FontSize = 11 };
        body.Children.Add(note);
        RefreshSelection();
    }

    void AddDevice(Panel host, string title, ComboBox box, string[] items, string? selected)
    {
        var column = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        column.Children.Add(new TextBlock { Text = title, Foreground = Muted, Margin = new Thickness(0, 0, 0, 5) });
        box.ItemsSource = items; box.Background = Card; box.Foreground = Brushes.White; box.Padding = new Thickness(8, 6, 8, 6);
        box.SelectedIndex = 0;
        if (title == "System audio" && RecorderAudioSource.IsWasapiLoopback(selected)) box.SelectedIndex = 1;
        else if (!string.IsNullOrWhiteSpace(selected))
        {
            int i = Array.FindIndex(items, x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) box.SelectedIndex = i;
        }
        column.Children.Add(box); host.Children.Add(column);
    }

    void ChooseMode(int mode)
    {
        _mode = mode;
        if (mode == 1 && _area == null || mode == 2 && _window == null) ChooseSource_Click(this, new RoutedEventArgs());
        RefreshSelection();
    }

    void ChooseSource_Click(object? sender, RoutedEventArgs e)
    {
        if (_mode == 1)
        {
            var picker = new CaptureAreaWindow { Owner = this };
            if (picker.ShowDialog() == true) _area = picker.SelectedArea;
        }
        else if (_mode == 2)
        {
            var picker = new CaptureWindowPicker { Owner = this };
            if (picker.ShowDialog() == true) _window = picker.SelectedWindow;
        }
        RefreshSelection();
    }

    void RefreshSelection()
    {
        for (int i = 0; i < _modeCards.Length; i++) _modeCards[i].Background = i == _mode ? Accent : Card;
        _areaLabel.Text = _mode == 1 ? _area is null ? "Select an area before recording" : $"Selected area · {_area.Width} × {_area.Height}" : "";
        _windowLabel.Text = _mode == 2 ? _window is null ? "Choose an open window before recording" : "Selected window · " + _window.Title : "";
    }

    bool ValidSource()
    {
        if (_mode == 1 && _area == null) { MessageBox.Show(this, "Choose the custom area to record first.", "Recording source", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        if (_mode == 2 && _window == null) { MessageBox.Show(this, "Choose the window to record first.", "Recording source", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        return true;
    }

    void Collect()
    {
        _options.WebcamDevice = _camera.SelectedIndex > 0 ? _camera.SelectedItem as string ?? "" : "";
        _options.MicDevice = _microphone.SelectedIndex > 0 ? _microphone.SelectedItem as string : null;
        _options.MicrophoneDevice = _options.MicDevice ?? "";
        _options.SystemAudioDevice = SelectedSystemAudio;
        _options.WebcamLayout = (WebcamLayout)Math.Clamp(_webcamLayout.SelectedIndex, 0, 2);
        _options.TrackMouse = _trackMouse.IsChecked == true;
        _options.TrackTyping = _trackMouse.IsChecked == true && _trackTyping.IsChecked == true;
        _options.SmartAssistEnabled = _smartAssist.IsChecked == true;
        _options.Area = SelectedArea; _options.Window = SelectedWindow;
    }

    static Button Button(string content, Brush fill, double width) => new()
    {
        Content = content, MinWidth = width, Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 0),
        Background = fill, Foreground = Brushes.White, BorderBrush = Brush("#3A404C"), BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex)!);
}
