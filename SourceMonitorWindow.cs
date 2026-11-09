using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VideoEditor.Models;

namespace VideoEditor;

public sealed class SourceMonitorWindow : Window
{
    readonly MediaItem _media;
    readonly MediaElement _player = new();
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(8) };
    readonly TextBlock _clock = new() { Foreground = Brushes.LightCyan, Margin = new Thickness(8) };
    readonly CheckBox _video = new() { Content = "Video", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(8) };
    readonly CheckBox _audio = new() { Content = "Audio", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(8) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    double _in, _out;
    bool _settingSeek;

    public event Action<MediaItem, double, double, bool, bool, bool>? SendToTimeline;

    public SourceMonitorWindow(MediaItem media)
    {
        _media = media;
        Title = "MyEditor · Source Monitor";
        Width = 900; Height = 590; MinWidth = 560; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 21, 29));
        Foreground = Brushes.White;
        _out = Math.Max(.1, media.Duration);
        _video.IsChecked = media.Kind != MediaKind.Audio;
        _audio.IsChecked = media.Kind == MediaKind.Audio || media.HasAudio;

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(12) };
        var controls = new StackPanel();
        DockPanel.SetDock(controls, Dock.Bottom);
        var title = new TextBlock { Text = media.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(8) };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var bottom = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(29, 30, 40)) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var seekRow = new DockPanel();
        DockPanel.SetDock(_clock, Dock.Right);
        seekRow.Children.Add(_clock); seekRow.Children.Add(_seek);
        bottom.Children.Add(seekRow);
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
        var play = MakeButton("Play / Pause", () => { if (_player.CanPause && _player.Position < _player.NaturalDuration.TimeSpan) _player.Pause(); else _player.Play(); });
        buttons.Children.Add(play);
        buttons.Children.Add(MakeButton("Set In", () => { _in = Math.Clamp(_player.Position.TotalSeconds, 0, _out - .05); UpdateClock(); }));
        buttons.Children.Add(MakeButton("Set Out", () => { _out = Math.Clamp(_player.Position.TotalSeconds, _in + .05, Math.Max(_in + .05, media.Duration)); UpdateClock(); }));
        buttons.Children.Add(new TextBlock { Text = "Send:", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, Margin = new Thickness(12, 0, 0, 0) });
        buttons.Children.Add(_video); buttons.Children.Add(_audio);
        buttons.Children.Add(MakeButton("Insert ( , )", () => Send(false)));
        buttons.Children.Add(MakeButton("Overwrite ( . )", () => Send(true)));
        bottom.Children.Add(buttons);
        root.Children.Add(bottom);

        var viewer = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), Margin = new Thickness(2, 2, 2, 8), Child = _player };
        root.Children.Add(viewer);
        Content = root;
        _player.LoadedBehavior = MediaState.Manual;
        _player.UnloadedBehavior = MediaState.Manual;
        _player.ScrubbingEnabled = true;
        _player.Stretch = Stretch.Uniform;
        try { _player.Source = new Uri(media.Path); _player.MediaOpened += Player_MediaOpened; _player.MediaFailed += (_, e) => _clock.Text = "Preview unavailable · " + e.ErrorException.Message; }
        catch (Exception ex) { _clock.Text = "Could not open source · " + ex.Message; }
        _seek.ValueChanged += (_, _) => { if (!_settingSeek) _player.Position = TimeSpan.FromSeconds(_seek.Value); };
        _timer.Tick += (_, _) => UpdateClock();
        Loaded += (_, _) => { _timer.Start(); _player.Play(); };
        Closed += (_, _) => { _timer.Stop(); _player.Stop(); _player.Source = null; };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Space) { if (_player.CanPause) _player.Pause(); else _player.Play(); e.Handled = true; } };
    }

    void Player_MediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_player.NaturalDuration.HasTimeSpan)
        {
            _out = Math.Max(.05, Math.Min(_out, _player.NaturalDuration.TimeSpan.TotalSeconds));
            _seek.Maximum = Math.Max(.05, _player.NaturalDuration.TimeSpan.TotalSeconds);
        }
        UpdateClock();
    }

    void UpdateClock()
    {
        double t = _player.Position.TotalSeconds;
        _settingSeek = true;
        _seek.Value = Math.Clamp(t, _seek.Minimum, _seek.Maximum);
        _settingSeek = false;
        _clock.Text = $"{Time(t)}  |  In {Time(_in)}  –  Out {Time(_out)}  |  Selected {Math.Max(0, _out - _in):0.00}s";
    }

    void Send(bool overwrite)
    {
        bool video = _video.IsChecked == true && _media.Kind != MediaKind.Audio;
        bool audio = _audio.IsChecked == true && (_media.Kind == MediaKind.Audio || _media.HasAudio);
        if (!video && !audio) { MessageBox.Show(this, "Choose video, audio, or both before sending the source.", "Source monitor"); return; }
        SendToTimeline?.Invoke(_media, _in, _out, video, audio, overwrite);
    }

    static Button MakeButton(string label, Action action)
    {
        var b = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(12, 7, 12, 7), Background = new SolidColorBrush(Color.FromRgb(48, 51, 69)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(83, 87, 118)) };
        b.Click += (_, _) => action();
        return b;
    }

    static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.ff");
}
