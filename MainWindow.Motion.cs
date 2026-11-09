using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoEditor.Models;
using VideoEditor.Services;

namespace VideoEditor;

/// <summary>
/// CapCut-style motion tools: per-property keyframes with easing, speed-curve presets, reverse, freeze frame,
/// shorts auto-reframe and caption styles. The panels are built in code and appended to the existing inspectors.
/// </summary>
public partial class MainWindow
{
    Expander? _motionExpander;
    StackPanel? _overlayMotionPanel;
    ComboBox? _mkProp, _mkEase, _mkSpeed, _capStyleBox, _capLangBox, _okEase;
    Slider? _mkValue, _mkFreezeSeconds, _okRot, _okOpacity;
    TextBlock? _mkInfo, _mkValueText, _mkReframeInfo, _okInfo;
    Button? _mkReverseBtn, _mkFreezeBtn, _mkReframeBtn, _mkSilenceBtn;
    Slider? _mkSilenceDb, _mkSilenceLen, _okSim;
    ComboBox? _okChroma, _okBlend;
    CheckBox? _mkLoudness;
    bool _motionBusy, _motionLoading;
    RotateTransform? _previewRotate;

    static readonly (string Label, string Code)[] CaptionLanguages =
    {
        ("Auto-detect", "auto"), ("বাংলা (Bangla)", "bn"), ("English", "en"), ("हिन्दी (Hindi)", "hi"), ("اردو (Urdu)", "ur")
    };

    // ------------------------------------------------------------------ build
    void BuildMotionPanel()
    {
        var info = (Brush)new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
        TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 6, 0, 2), Foreground = info };
        TextBlock Note(string t) => new() { Text = t, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = info, Margin = new Thickness(0, 2, 0, 4) };
        Button Btn(string t, RoutedEventHandler h, string style = "BlueActionButton")
        {
            var b = new Button { Content = t, Margin = new Thickness(0, 3, 6, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
            if (TryFindResource(style) is Style st) b.Style = st;
            b.Click += h;
            return b;
        }

        var root = new StackPanel { Margin = new Thickness(8, 4, 4, 4) };

        // --- keyframes
        root.Children.Add(new TextBlock { Text = "Keyframes (any property)", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(Label("Property"));
        _mkProp = new ComboBox { ItemsSource = ClipMotion.Props.Select(p => p.Name).ToList(), SelectedIndex = 0 };
        _mkProp.SelectionChanged += (_, _) => { if (!_motionLoading) SyncMotionValue(); };
        root.Children.Add(_mkProp);
        root.Children.Add(Label("Value at playhead"));
        _mkValue = new Slider { Minimum = 1, Maximum = 3, Value = 1 };
        _mkValue.ValueChanged += (_, _) => { if (_mkValueText != null && _mkValue != null) _mkValueText.Text = _mkValue.Value.ToString("0.###"); };
        root.Children.Add(_mkValue);
        _mkValueText = new TextBlock { Foreground = info, Text = "1" };
        root.Children.Add(_mkValueText);
        root.Children.Add(Label("Easing of the segment after this key"));
        _mkEase = new ComboBox { ItemsSource = EasingCurves.Names, SelectedItem = "Ease in-out" };
        root.Children.Add(_mkEase);
        var keyRow = new WrapPanel();
        keyRow.Children.Add(Btn("◆ Set key at playhead", MotionSetKey_Click));
        keyRow.Children.Add(Btn("✕ Remove key here", MotionRemoveKey_Click, "VioletActionButton"));
        keyRow.Children.Add(Btn("Clear property", MotionClearProp_Click, "VioletActionButton"));
        root.Children.Add(keyRow);
        _mkInfo = Note("Keys: 0");
        root.Children.Add(_mkInfo);
        root.Children.Add(Note("Move the playhead inside the clip, pick a property, set a value, press “Set key”. Two or more keys animate. Zoom, pan and speed show live in the preview; rotation, colour and volume keys are applied in the exported video."));

        // --- speed curve
        root.Children.Add(new TextBlock { Text = "Speed curve", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
        _mkSpeed = new ComboBox { ItemsSource = SpeedCurves.Names, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(_mkSpeed);
        root.Children.Add(Btn("⚡ Apply speed curve", MotionSpeedCurve_Click, "AmberActionButton"));
        root.Children.Add(Note("Cuts the clip into short pieces with stepped speeds (slow-mo ↔ fast). You can still fine-tune each piece with the Speed slider. Not available on clips that have keyframes."));

        // --- reverse / freeze
        root.Children.Add(new TextBlock { Text = "Reverse & freeze", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
        var rf = new WrapPanel();
        _mkReverseBtn = Btn("⏪ Reverse clip", MotionReverse_Click, "AmberActionButton");
        rf.Children.Add(_mkReverseBtn);
        _mkFreezeBtn = Btn("❄ Freeze frame here", MotionFreeze_Click, "AmberActionButton");
        rf.Children.Add(_mkFreezeBtn);
        root.Children.Add(rf);
        root.Children.Add(Label("Freeze length (seconds)"));
        _mkFreezeSeconds = new Slider { Minimum = 0.5, Maximum = 8, Value = 2, TickFrequency = 0.5, IsSnapToTickEnabled = true };
        root.Children.Add(_mkFreezeSeconds);
        root.Children.Add(Note("Reverse makes a reversed copy of the trimmed part (done in 3-second chunks to keep memory low). Freeze frame splits the clip at the playhead and inserts the still."));

        // --- audio
        root.Children.Add(new TextBlock { Text = "Audio", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
        _mkLoudness = new CheckBox { Content = "Normalize loudness (−16 LUFS, social-media level)", Margin = new Thickness(0, 4, 0, 0), Foreground = Brushes.White };
        _mkLoudness.Click += (_, _) =>
        {
            if (_motionLoading || _selected is not { IsGap: false } c) return;
            if (IsObjectLocked(c)) { Status.Text = "Unlock V1 before changing audio"; return; }
            Checkpoint(); c.NormalizeLoudness = _mkLoudness.IsChecked == true; AfterEdit();
            Status.Text = c.NormalizeLoudness ? "Loudness will be normalized on export" : "Loudness normalization off";
        };
        root.Children.Add(_mkLoudness);
        root.Children.Add(Note("Evens out quiet and loud clips so they sound the same level. Applied when you export; for noise, also tick “Denoise audio” above."));

        // --- auto-cut silences
        root.Children.Add(new TextBlock { Text = "Auto-cut silences", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(Label("Counts as silence below (dB) — lower = only very quiet parts"));
        _mkSilenceDb = new Slider { Minimum = -60, Maximum = -20, Value = -35, TickFrequency = 1, IsSnapToTickEnabled = true };
        root.Children.Add(_mkSilenceDb);
        root.Children.Add(Label("Shortest silence to remove (seconds)"));
        _mkSilenceLen = new Slider { Minimum = 0.2, Maximum = 2, Value = 0.5, TickFrequency = 0.1, IsSnapToTickEnabled = true };
        root.Children.Add(_mkSilenceLen);
        _mkSilenceBtn = Btn("✂ Remove silences", MotionSilence_Click, "TealActionButton");
        root.Children.Add(_mkSilenceBtn);
        root.Children.Add(Note("Finds quiet gaps with FFmpeg and cuts them out, keeping a short pause around speech. Do this before adding captions and overlays, because later items do not move. Undo restores the clip."));

        // --- shorts
        root.Children.Add(new TextBlock { Text = "Shorts / Reels auto-reframe", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
        var rr = new WrapPanel();
        _mkReframeBtn = Btn("📱 Analyze & reframe to 9:16", MotionReframe_Click, "TealActionButton");
        rr.Children.Add(_mkReframeBtn);
        rr.Children.Add(Btn("Remove reframe", MotionRemoveReframe_Click, "VioletActionButton"));
        root.Children.Add(rr);
        _mkReframeInfo = Note("Not analysed.");
        root.Children.Add(_mkReframeInfo);
        root.Children.Add(Note("Follows the screen-recording mouse, or the strongest motion/detail in other videos (not face recognition). The preview shows the full frame; the exported vertical video follows the subject."));

        // --- captions
        root.Children.Add(new TextBlock { Text = "Auto-caption language & style", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
        _capLangBox = new ComboBox { ItemsSource = CaptionLanguages.Select(l => l.Label).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 0) };
        _capLangBox.SelectionChanged += (_, _) =>
        {
            int i = Math.Clamp(_capLangBox.SelectedIndex, 0, CaptionLanguages.Length - 1);
            LocalWhisperService.Language = CaptionLanguages[i].Code;
        };
        root.Children.Add(_capLangBox);
        _capStyleBox = new ComboBox { ItemsSource = CaptionStyles.Names, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 0) };
        _capStyleBox.SelectionChanged += (_, _) => CaptionStyles.Current = _capStyleBox.SelectedItem as string ?? CaptionStyles.Names[0];
        root.Children.Add(_capStyleBox);
        root.Children.Add(Note("Used the next time you press the local AI captions button. Choosing Bangla tells the speech model the language, which is more accurate than auto-detect; the small built-in “base” model can still make mistakes with Bangla."));

        _motionExpander = new Expander
        {
            Header = "🎞 Keyframes · Speed curve · Reverse · Shorts",
            IsExpanded = false,
            Margin = new Thickness(0, 3, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xB8, 0xFF)),
            Content = root
        };
        Inspector.Children.Add(_motionExpander);

        BuildOverlayMotionPanel(info, Btn, Label, Note);
        EnsurePreviewRotate();
    }

    void BuildOverlayMotionPanel(Brush info, Func<string, RoutedEventHandler, string, Button> Btn, Func<string, TextBlock> Label, Func<string, TextBlock> Note)
    {
        _overlayMotionPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var exp = new StackPanel { Margin = new Thickness(8, 4, 4, 4) };
        exp.Children.Add(Label("Rotation (degrees)"));
        _okRot = new Slider { Minimum = -360, Maximum = 360, Value = 0 };
        exp.Children.Add(_okRot);
        exp.Children.Add(Label("Opacity"));
        _okOpacity = new Slider { Minimum = 0, Maximum = 1, Value = 1 };
        exp.Children.Add(_okOpacity);
        exp.Children.Add(Label("Easing of the segment after this key"));
        _okEase = new ComboBox { ItemsSource = EasingCurves.Names, SelectedItem = "Ease in-out" };
        exp.Children.Add(_okEase);
        exp.Children.Add(Btn("◆ Set rotation / opacity / easing key", OverlayMotionKey_Click, "BlueActionButton"));
        exp.Children.Add(new TextBlock { Text = "Green screen & blend mode", FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) });
        exp.Children.Add(Label("Make this colour transparent"));
        _okChroma = new ComboBox { ItemsSource = new[] { "None", "Green", "Blue" }, SelectedIndex = 0 };
        exp.Children.Add(_okChroma);
        exp.Children.Add(Label("Colour tolerance (higher removes more shades)"));
        _okSim = new Slider { Minimum = 0.05, Maximum = 0.6, Value = 0.3 };
        exp.Children.Add(_okSim);
        exp.Children.Add(Label("Blend mode"));
        _okBlend = new ComboBox { ItemsSource = new[] { "Normal", "Screen", "Multiply", "Overlay", "Soft light", "Add", "Lighten", "Darken", "Difference" }, SelectedIndex = 0 };
        exp.Children.Add(_okBlend);
        exp.Children.Add(Btn("Apply green screen / blend", OverlayLookApply_Click, "TealActionButton"));
        exp.Children.Add(Note("Screen and Add make black disappear (good for light leaks, fire, glitch clips). Multiply makes white disappear. Both effects are applied in the exported video; the preview shows the overlay normally."));
        _okInfo = Note("Uses the position and size currently shown for the playhead, so add normal keyframes first. Easing applies to position and size too. Rotation and opacity keys are applied in the exported video (picture/video overlays).");
        exp.Children.Add(_okInfo);
        _overlayMotionPanel.Children.Add(new Expander
        {
            Header = "🔄 Rotation · Opacity · Green screen · Blend",
            IsExpanded = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xB8, 0xFF)),
            Content = exp
        });

        var caption = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        caption.Children.Add(Label("Caption style"));
        var styleBox = new ComboBox { ItemsSource = CaptionStyles.Names, SelectedIndex = 0, Name = "OverlayCaptionStyle" };
        caption.Children.Add(styleBox);
        var row = new WrapPanel();
        row.Children.Add(Btn("Apply to this text", (_, _) => ApplyCaptionStyle(styleBox.SelectedItem as string, false), "TealActionButton"));
        row.Children.Add(Btn("Apply to all text on this lane", (_, _) => ApplyCaptionStyle(styleBox.SelectedItem as string, true), "TealActionButton"));
        caption.Children.Add(row);
        caption.Visibility = Visibility.Visible;
        caption.Tag = "caption";
        _overlayMotionPanel.Children.Add(caption);

        LayerPanel.Children.Add(_overlayMotionPanel);
    }

    // ------------------------------------------------------------------ refresh (called from LoadInspector)
    void RefreshMotionPanel()
    {
        if (_motionExpander == null) return;
        _motionLoading = true;
        try
        {
            var clip = _selected;
            bool videoClip = clip is { IsGap: false };
            _motionExpander.Visibility = videoClip ? Visibility.Visible : Visibility.Collapsed;
            if (clip != null && videoClip)
            {
                bool real = !clip.IsImage;
                if (_mkReverseBtn != null) _mkReverseBtn.IsEnabled = real;
                if (_mkReframeBtn != null) _mkReframeBtn.IsEnabled = real;
                if (_mkReframeInfo != null)
                    _mkReframeInfo.Text = clip.AutoReframe && clip.ReframePoints.Count >= 2
                        ? $"Active: {clip.ReframePoints.Count} tracking points. Exported video follows them."
                        : "Not analysed.";
                SyncMotionValue();
            }

            if (_mkLoudness != null && clip != null) _mkLoudness.IsChecked = clip.NormalizeLoudness;
            if (_selOv is { IsText: false } look && _okChroma != null && _okSim != null && _okBlend != null)
            {
                _okChroma.SelectedItem = look.ChromaColor;
                _okSim.Value = Math.Clamp(look.ChromaSimilarity, _okSim.Minimum, _okSim.Maximum);
                _okBlend.SelectedItem = look.BlendMode;
            }
            if (_overlayMotionPanel != null)
            {
                _overlayMotionPanel.Visibility = _selOv != null ? Visibility.Visible : Visibility.Collapsed;
                if (_overlayMotionPanel.Children.Count > 0 && _overlayMotionPanel.Children[0] is Expander ex)
                    ex.Visibility = _selOv is { IsText: false } ? Visibility.Visible : Visibility.Collapsed;
                foreach (var child in _overlayMotionPanel.Children.OfType<StackPanel>())
                    if (child.Tag as string == "caption") child.Visibility = _selOv is { IsText: true } ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally { _motionLoading = false; }
    }

    /// <summary>Playhead position inside the selected clip (seconds of clip time), or null if the playhead is elsewhere.</summary>
    double? LocalTimeInSelected()
    {
        if (_selected == null) return null;
        var (clip, _, start) = Locate(_playhead);
        if (!ReferenceEquals(clip, _selected)) return null;
        return Math.Clamp(_playhead - start, 0, clip.Duration);
    }

    void SyncMotionValue()
    {
        if (_selected == null || _mkProp == null || _mkValue == null) return;
        var info = ClipMotion.Info(_mkProp.SelectedItem as string ?? ClipMotion.Zoom);
        bool was = _motionLoading; _motionLoading = true;
        _mkValue.Minimum = info.Min; _mkValue.Maximum = info.Max; _mkValue.SmallChange = info.Step; _mkValue.LargeChange = info.Step * 10;
        double? local = LocalTimeInSelected();
        double v = local.HasValue ? _selected.ValueAt(info.Name, local.Value) : _selected.BaseValue(info.Name);
        _mkValue.Value = Math.Clamp(v, info.Min, info.Max);
        if (_mkValueText != null) _mkValueText.Text = _mkValue.Value.ToString("0.###");
        int keys = _selected.Keys.TryGetValue(info.Name, out var list) ? list.Count : 0;
        if (_mkInfo != null) _mkInfo.Text = $"{info.Name}: {keys} key(s). {info.Hint}.";
        _motionLoading = was;
    }

    // ------------------------------------------------------------------ keyframes
    void MotionSetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsGap: false } clip || _mkProp == null || _mkValue == null) return;
        if (IsObjectLocked(clip)) { Status.Text = "Unlock V1 before adding keyframes"; return; }
        double? local = LocalTimeInSelected();
        if (local == null) { Status.Text = "Move the playhead inside the selected clip to add a keyframe"; return; }
        Pause(); Checkpoint();
        clip.SetKey(_mkProp.SelectedItem as string ?? ClipMotion.Zoom, local.Value, _mkValue.Value, _mkEase?.SelectedItem as string ?? "Ease in-out");
        AfterEdit();
        Status.Text = $"Keyframe set at {local.Value:0.00}s";
    }

    void MotionRemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsGap: false } clip || _mkProp == null) return;
        double? local = LocalTimeInSelected();
        if (local == null) { Status.Text = "Move the playhead onto a keyframe of the selected clip"; return; }
        string prop = _mkProp.SelectedItem as string ?? ClipMotion.Zoom;
        Pause(); Checkpoint();
        if (!clip.RemoveKeyNear(prop, local.Value)) { Status.Text = "No keyframe at the playhead for this property"; return; }
        AfterEdit();
        Status.Text = "Keyframe removed";
    }

    void MotionClearProp_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsGap: false } clip || _mkProp == null) return;
        string prop = _mkProp.SelectedItem as string ?? ClipMotion.Zoom;
        if (!clip.HasKeys(prop)) return;
        Pause(); Checkpoint();
        clip.Keys.Remove(prop);
        AfterEdit();
        Status.Text = $"{prop} keyframes cleared";
    }

    // ------------------------------------------------------------------ speed curve
    void MotionSpeedCurve_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsGap: false, IsImage: false } clip) { Status.Text = "Select a video clip first"; return; }
        if (IsObjectLocked(clip)) { Status.Text = "Unlock V1 before changing speed"; return; }
        if (clip.HasKeys()) { Status.Text = "Clear this clip's keyframes first: a speed curve splits the clip into pieces"; return; }
        if (clip.Out - clip.In < 0.4) { Status.Text = "The clip is too short for a speed curve"; return; }
        string preset = _mkSpeed?.SelectedItem as string ?? SpeedCurves.Names[0];
        Pause(); Checkpoint();
        var pieces = SpeedCurves.Apply(clip, preset);
        int index = _timeline.IndexOf(clip);
        if (index < 0) return;
        _timeline.RemoveAt(index);
        _timeline.InsertRange(index, pieces);
        ClearSelection(); _selected = pieces[0];
        AfterEdit();
        Status.Text = $"Speed curve “{preset}” applied: {pieces.Count} pieces";
    }

    // ------------------------------------------------------------------ reverse
    async void MotionReverse_Click(object sender, RoutedEventArgs e)
    {
        if (_motionBusy) return;
        if (_selected is not { IsGap: false, IsImage: false } clip) { Status.Text = "Select a video clip first"; return; }
        if (IsObjectLocked(clip)) { Status.Text = "Unlock V1 before reversing"; return; }
        double length = clip.Out - clip.In;
        if (length > 180 && MessageBox.Show(this, $"This reverses {length:0} seconds of video, which can take a while. Continue?",
                "Reverse clip", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Pause();
        _motionBusy = true;
        var snapshot = clip.Clone();
        try
        {
            if (!RuntimeModuleService.IsInstalled(RuntimeModule.MediaEngine)) await RuntimeModuleService.EnsureAsync(RuntimeModule.MediaEngine);
            Status.Text = "Reversing…";
            var progress = new Progress<double>(p => Status.Text = $"Reversing… {p * 100:0}%");
            string path = await ClipTools.ReverseRangeAsync(snapshot.Path, snapshot.In, length, snapshot.HasAudio, progress, CancellationToken.None);
            var probe = await FFmpegService.ProbeAsync(path);
            int index = _timeline.IndexOf(clip);
            if (index < 0) { Status.Text = "The clip was removed while reversing"; return; }
            Checkpoint();
            var events = ClipTools.ReversePointerEvents(snapshot);
            double dur = probe.Duration > 0.1 ? probe.Duration : length;
            clip.Path = path; clip.In = 0; clip.Out = dur; clip.SourceDuration = dur;
            clip.PointerEvents = events;
            clip.ReframePoints = new List<ReframePoint>(); clip.AutoReframe = false; // analysed on the old direction
            AfterEdit();
            Status.Text = "Clip reversed (press again to flip it back)";
        }
        catch (Exception ex)
        {
            Status.Text = "Reverse failed";
            MessageBox.Show(this, "The clip could not be reversed.\n\n" + ex.Message, "Reverse clip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _motionBusy = false; }
    }

    // ------------------------------------------------------------------ freeze frame
    async void MotionFreeze_Click(object sender, RoutedEventArgs e)
    {
        if (_motionBusy) return;
        var (clip, sourceTime, start) = Locate(_playhead);
        if (clip == null || clip.IsGap || clip.IsImage) { Status.Text = "Put the playhead on a video clip to freeze a frame"; return; }
        if (IsObjectLocked(clip)) { Status.Text = "Unlock V1 before freezing a frame"; return; }
        Pause();
        _motionBusy = true;
        double seconds = _mkFreezeSeconds?.Value ?? 2;
        try
        {
            if (!RuntimeModuleService.IsInstalled(RuntimeModule.MediaEngine)) await RuntimeModuleService.EnsureAsync(RuntimeModule.MediaEngine);
            Status.Text = "Capturing frame…";
            string png = await ClipTools.ExtractFrameAsync(clip.Path, sourceTime, CancellationToken.None);
            int index = _timeline.IndexOf(clip);
            if (index < 0) return;
            Checkpoint();
            double local = _playhead - start;
            var freeze = new Clip { Path = png, IsImage = true, SourceDuration = seconds, In = 0, Out = seconds, HasAudio = false };
            if (local < 0.05) _timeline.Insert(index, freeze);
            else if (local > clip.Duration - 0.05) _timeline.Insert(index + 1, freeze);
            else
            {
                var right = clip.Clone();
                ClipMotion.SplitKeys(clip, right, local);
                clip.Out = sourceTime; right.In = sourceTime; right.TransitionIn = "None";
                _timeline.Insert(index + 1, freeze);
                _timeline.Insert(index + 2, right);
            }
            ClearSelection(); _selected = freeze;
            AfterEdit();
            Status.Text = $"Frozen for {seconds:0.#}s";
        }
        catch (Exception ex)
        {
            Status.Text = "Freeze frame failed";
            MessageBox.Show(this, "The frame could not be captured.\n\n" + ex.Message, "Freeze frame", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _motionBusy = false; }
    }

    // ------------------------------------------------------------------ auto-cut silences
    async void MotionSilence_Click(object sender, RoutedEventArgs e)
    {
        if (_motionBusy) return;
        if (_selected is not { IsGap: false, IsImage: false, HasAudio: true } clip) { Status.Text = "Select a video clip that has sound"; return; }
        if (IsObjectLocked(clip)) { Status.Text = "Unlock V1 before cutting"; return; }
        if (clip.HasKeys()) { Status.Text = "Clear this clip's keyframes first: cutting splits the clip into pieces"; return; }
        Pause();
        _motionBusy = true;
        try
        {
            if (!RuntimeModuleService.IsInstalled(RuntimeModule.MediaEngine)) await RuntimeModuleService.EnsureAsync(RuntimeModule.MediaEngine);
            Status.Text = "Listening for silences…";
            double length = clip.Out - clip.In;
            var silences = await ClipTools.DetectSilencesAsync(clip.Path, clip.In, length,
                _mkSilenceDb?.Value ?? -35, _mkSilenceLen?.Value ?? 0.5, CancellationToken.None);
            int index = _timeline.IndexOf(clip);
            if (index < 0) return;
            if (silences.Count == 0) { Status.Text = "No silences found at this setting"; return; }
            var keep = ClipTools.KeepRanges(silences, length);
            if (keep.Count == 0) { Status.Text = "Everything counts as silence at this setting. Lower the dB value."; return; }
            double removed = length - keep.Sum(k => k.End - k.Start);
            Checkpoint();
            var pieces = new List<Clip>();
            for (int i = 0; i < keep.Count; i++)
            {
                var piece = clip.Clone();
                piece.In = clip.In + keep[i].Start;
                piece.Out = clip.In + keep[i].End;
                if (i > 0) { piece.TransitionIn = "None"; piece.OverlayText = ""; }
                pieces.Add(piece);
            }
            _timeline.RemoveAt(index);
            _timeline.InsertRange(index, pieces);
            ClearSelection(); _selected = pieces[0];
            AfterEdit();
            Status.Text = $"Removed {removed:0.0}s of silence ({silences.Count} gaps) · {pieces.Count} clips";
        }
        catch (Exception ex)
        {
            Status.Text = "Silence detection failed";
            MessageBox.Show(this, "The clip could not be analysed.\n\n" + ex.Message, "Auto-cut silences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _motionBusy = false; }
    }

    /// <summary>Shows which FFmpeg build is running and whether a GPU encoder works.</summary>
    async Task ShowEngineSummaryAsync()
    {
        try
        {
            var report = await MediaEngine.InspectAsync(FFmpegService.ResolveTool("ffmpeg"));
            Status.Text = "Ready · " + report.Summary;
        }
        catch { /* purely informational */ }
    }

    // ------------------------------------------------------------------ shorts auto-reframe
    async void MotionReframe_Click(object sender, RoutedEventArgs e)
    {
        if (_motionBusy) return;
        if (_selected is not { IsGap: false, IsImage: false } clip) { Status.Text = "Select a video clip first"; return; }
        if (IsObjectLocked(clip)) { Status.Text = "Unlock V1 before reframing"; return; }
        Pause();
        _motionBusy = true;
        try
        {
            if (!RuntimeModuleService.IsInstalled(RuntimeModule.MediaEngine)) await RuntimeModuleService.EnsureAsync(RuntimeModule.MediaEngine);
            Status.Text = "Analysing the clip…";
            var progress = new Progress<double>(p => Status.Text = $"Analysing the clip… {p * 100:0}%");
            var points = await AutoReframeService.AnalyzeAsync(clip, progress, CancellationToken.None);
            if (_timeline.IndexOf(clip) < 0) return;
            if (points.Count < 2) { Status.Text = "Nothing to follow in this clip"; return; }
            Checkpoint();
            clip.ReframePoints = points; clip.AutoReframe = true;
            AfterEdit();
            Status.Text = $"Reframe ready ({points.Count} points)";
            if (AspectBox.SelectedIndex != 1 &&
                MessageBox.Show(this, "Switch the project to Portrait (9:16) so the export is a vertical short?", "Auto-reframe",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                AspectBox.SelectedIndex = 1;
        }
        catch (Exception ex)
        {
            Status.Text = "Reframe analysis failed";
            MessageBox.Show(this, "The clip could not be analysed.\n\n" + ex.Message, "Auto-reframe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _motionBusy = false; }
    }

    void MotionRemoveReframe_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } clip || !clip.AutoReframe) return;
        Pause(); Checkpoint();
        clip.AutoReframe = false; clip.ReframePoints = new List<ReframePoint>();
        AfterEdit();
        Status.Text = "Auto-reframe removed";
    }

    // ------------------------------------------------------------------ overlay keys & caption styles
    void OverlayMotionKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv is not { IsText: false } o || _okRot == null || _okOpacity == null) return;
        if (IsObjectLocked(o)) { Status.Text = "Unlock this track before adding keyframes"; return; }
        double rel = _playhead - o.Start;
        if (rel < 0 || rel > o.Duration) { Status.Text = "Move the playhead inside the selected overlay"; return; }
        Pause(); Checkpoint();
        var key = o.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - rel) < 0.05);
        if (key == null)
        {
            var (x, y, s) = o.At(rel);
            o.Keyframes.Add(key = new Keyframe { Time = rel, X = x, Y = y, Scale = s });
        }
        key.Rotation = _okRot.Value;
        key.Opacity = _okOpacity.Value;
        key.Easing = _okEase?.SelectedItem as string ?? "Linear";
        o.Keyframes = o.Keyframes.OrderBy(k => k.Time).ToList();
        AfterEdit();
        Status.Text = "Overlay key set (rotation, opacity, easing)";
    }

    void OverlayLookApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv is not { IsText: false } o || _okChroma == null || _okSim == null || _okBlend == null) return;
        if (IsObjectLocked(o)) { Status.Text = "Unlock this track before changing the overlay"; return; }
        Pause(); Checkpoint();
        o.ChromaColor = _okChroma.SelectedItem as string ?? "None";
        o.ChromaSimilarity = _okSim.Value;
        o.BlendMode = _okBlend.SelectedItem as string ?? "Normal";
        AfterEdit();
        Status.Text = $"Green screen: {o.ChromaColor} · Blend: {o.BlendMode} (visible in the exported video)";
    }

    void ApplyCaptionStyle(string? style, bool wholeLane)
    {
        if (_selOv is not { IsText: true } text || style == null) return;
        Pause(); Checkpoint();
        var targets = wholeLane ? _overlays.Where(o => o.IsText && o.TextTrack == text.TextTrack).ToList() : new List<OverlayClip> { text };
        foreach (var t in targets) CaptionStyles.Apply(t, style);
        AfterEdit();
        Status.Text = $"Style “{style}” applied to {targets.Count} text clip(s)";
    }

    /// <summary>Call after generating captions so they use the style chosen in the panel.</summary>
    void StyleNewCaptions(IEnumerable<OverlayClip> captions)
    {
        if (CaptionStyles.Current == CaptionStyles.Names[0]) return;
        foreach (var c in captions) CaptionStyles.Apply(c, CaptionStyles.Current);
    }

    // ------------------------------------------------------------------ live preview of zoom / pan / rotation keys
    void EnsurePreviewRotate()
    {
        try
        {
            if (_previewRotate == null && PreviewVideoLayer.RenderTransform is TransformGroup group && !group.IsFrozen)
            {
                _previewRotate = new RotateTransform(0);
                group.Children.Insert(0, _previewRotate);
            }
        }
        catch { _previewRotate = null; }
    }

    void SetPreviewRotation(double degrees)
    {
        if (_previewRotate != null && Math.Abs(_previewRotate.Angle - degrees) > 0.0001) _previewRotate.Angle = degrees;
    }

    /// <summary>Returns true when the clip has zoom/pan/rotation keys and the preview transform was set from them.</summary>
    bool ApplyKeyframePreview(Clip clip, double sourceTime)
    {
        if (!clip.HasKeys(ClipMotion.Zoom) && !clip.HasKeys(ClipMotion.PanX) && !clip.HasKeys(ClipMotion.PanY) && !clip.HasKeys(ClipMotion.Rotation))
        {
            SetPreviewRotation(0);
            return false;
        }
        double local = Math.Max(0, (sourceTime - clip.In) / clip.Speed);
        double zoom = Math.Clamp(clip.ValueAt(ClipMotion.Zoom, local), 1, 3);
        double px = Math.Clamp(clip.ValueAt(ClipMotion.PanX, local), 0, 1);
        double py = Math.Clamp(clip.ValueAt(ClipMotion.PanY, local), 0, 1);
        SetPreviewRotation(clip.ValueAt(ClipMotion.Rotation, local));
        PreviewZoomScale.ScaleX = PreviewZoomScale.ScaleY = zoom;

        double aspect = 16.0 / 9.0;
        if (clip.IsImage && PreviewImage.Source is System.Windows.Media.Imaging.BitmapSource bitmap && bitmap.PixelHeight > 0)
            aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
        else if (Preview.NaturalVideoWidth > 0 && Preview.NaturalVideoHeight > 0)
            aspect = (double)Preview.NaturalVideoWidth / Preview.NaturalVideoHeight;
        double fw = PreviewHost.ActualWidth, fh = PreviewHost.ActualHeight;
        if (fw <= 0 || fh <= 0) return true;
        double frameW = fw, frameH = fw / aspect;
        if (frameH > fh) { frameH = fh; frameW = fh * aspect; }
        double ox = (fw - frameW) / 2, oy = (fh - frameH) / 2;
        double focusX = ox + px * frameW - fw / 2;
        double focusY = oy + py * frameH - fh / 2;
        PreviewZoomTranslate.X = Math.Clamp(-zoom * focusX, -(zoom - 1) * fw / 2, (zoom - 1) * fw / 2);
        PreviewZoomTranslate.Y = Math.Clamp(-zoom * focusY, -(zoom - 1) * fh / 2, (zoom - 1) * fh / 2);
        return true;
    }
}
