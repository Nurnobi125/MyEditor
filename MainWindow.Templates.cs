using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VideoEditor.Models;

namespace VideoEditor;

sealed record FullVideoTemplate(string Name, string Description, string Background, string Palette,
    string Accent, string Shape, string Motion, string Eyebrow, string Beat, string Ending);

/// <summary>Visual template gallery: every title template is shown as a small preview card.</summary>
public partial class MainWindow
{
    readonly List<TextBlock> _templateSamples = new();
    readonly List<TextBlock> _animationSamples = new();

    static readonly FullVideoTemplate[] FullVideoTemplates =
    {
        new("Neon product promo", "Product launch · bold neon scenes", "Tech grid", "Violet", "Cyan", "Hexagon", "Zoom In", "NOW INTRODUCING", "Made to make a difference", "AVAILABLE NOW"),
        new("Cinematic brand story", "Brand intro · dramatic spotlight", "Spotlight", "Midnight", "Gold", "Ring", "Fade In", "A STORY WORTH SHARING", "Built around your big idea", "THE STORY STARTS HERE"),
        new("Creator / channel intro", "Creator opener · vibrant gradients", "Aurora", "Sunset", "Pink", "Sparkle", "Spin In", "WELCOME TO MY CHANNEL", "Fresh ideas. New moments.", "SUBSCRIBE FOR MORE"),
        new("Quick tutorial", "How-to · clear step-by-step cards", "Mesh gradient", "Aqua", "Cyan", "Rounded square", "Slide Up", "QUICK GUIDE", "Three simple steps to get started", "YOU'VE GOT THIS"),
        new("Event announcement", "Event · energetic announcement", "Sunburst", "Ruby", "Gold", "Star", "Bounce", "SAVE THE DATE", "A moment you won't want to miss", "SEE YOU THERE"),
        new("Travel / photo story", "Travel · calm cinematic cards", "Waves", "Emerald", "White", "Circle", "Drift", "POSTCARDS FROM EVERYWHERE", "Collect moments, not things", "UNTIL THE NEXT ADVENTURE")
    };

    void InitTemplateGallery()
    {
        VideoTemplateBox.ItemsSource = FullVideoTemplates.Select(t => t.Name).ToArray();
        VideoTemplateBox.SelectedIndex = 0;
        TemplateCategoryBox.ItemsSource = new[] { "All" }.Concat(TextTemplates.Select(t => t.Category).Distinct()).ToArray();
        TemplateCategoryBox.SelectedIndex = 0;
        TemplateCategoryBox.SelectionChanged += (_, _) => BuildTemplateCards();
        TemplateTitleText.TextChanged += (_, _) =>
        {
            string sample = SampleTitle();
            foreach (var tb in _templateSamples) tb.Text = sample;
        };
        TemplateCardList.SelectionChanged += (_, _) =>
        {
            if (TemplateCardList.SelectedItem is ListBoxItem { Tag: string name }) TemplateShelfBox.SelectedItem = name;
        };
        TemplateCardList.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(TemplateCardList, d) is ListBoxItem)
                AddStyledTitle_Click(this, new RoutedEventArgs());
        };
        AnimationCategoryBox.ItemsSource = new[] { "All" }.Concat(AnimationFamilies).ToArray();
        AnimationCategoryBox.SelectedIndex = 0;
        AnimationCategoryBox.SelectionChanged += (_, _) => BuildAnimationCards();
        AnimationCardList.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(AnimationCardList, d) is ListBoxItem item)
            {
                AnimationCardList.SelectedItem = item;
                ApplyAnimationCard_Click(this, new RoutedEventArgs());
            }
        };
        TemplateTitleText.TextChanged += (_, _) =>
        {
            foreach (var sample in _animationSamples) sample.Text = SampleTitle();
        };
        BuildTemplateCards();
        BuildAnimationCards();
    }

    void CreateVideoTemplate_Click(object sender, RoutedEventArgs e)
    {
        int index = Math.Clamp(VideoTemplateBox.SelectedIndex, 0, FullVideoTemplates.Length - 1);
        var template = FullVideoTemplates[index];
        if (_timeline.Count > 0 || _overlays.Count > 0 || _audios.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"Create ‘{template.Name}’ as a new 15-second edit?\n\nThis replaces the current timeline clips, titles and audio. Your imported media library stays available.",
                "Create a full video from template", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        try
        {
            Pause(); Checkpoint(); ClearSelection();
            _timeline.Clear(); _overlays.Clear(); _audios.Clear();
            string userTitle = string.IsNullOrWhiteSpace(TemplateTitleText.Text) || TemplateTitleText.Text == "Your title"
                ? template.Name : TemplateTitleText.Text.Trim();
            string[] backgrounds = { template.Background, "Glass cards", "Radial glow" };
            string[] headings = { userTitle, template.Beat, template.Ending };
            string[] eyebrows = { template.Eyebrow, "YOUR NEXT CHAPTER", "THANK YOU FOR WATCHING" };
            string[] motions = { template.Motion + " · 03", "Slide Left · 03", "Pop · 03" };
            string[] accentMotions = { "Drift · 03", "Pulse · 03", "Zoom In · 03" };
            int aspect = Math.Clamp(AspectBox.SelectedIndex, 0, 2);

            for (int scene = 0; scene < backgrounds.Length; scene++)
            {
                double start = scene * 5.0;
                var backgroundAsset = DesignCatalog.Backgrounds.First(a => a.Design == backgrounds[scene] && a.Palette == template.Palette);
                string backgroundPath = DesignCatalog.CreateAsset(backgroundAsset, aspect);
                _timeline.Add(new Clip
                {
                    Path = backgroundPath, IsImage = true, HasAudio = false,
                    SourceDuration = 5, In = 0, Out = 5,
                    TransitionIn = "None"
                });

                var accentAsset = DesignCatalog.Shapes.First(a => a.Design == template.Shape && a.Palette == template.Palette && a.Style == "Neon");
                string accentPath = DesignCatalog.CreateAsset(accentAsset, aspect);
                _overlays.Add(new OverlayClip
                {
                    Path = accentPath, IsImage = true, Start = start + 0.25, Duration = 4.5,
                    SourceDuration = 4.5, AspectRatio = DesignCatalog.AspectOf(accentAsset, aspect),
                    X = scene == 1 ? 0.16 : 0.84, Y = scene == 1 ? 0.28 : 0.30,
                    Scale = 0.17, Opacity = 0.92, AnimationPreset = accentMotions[scene], VideoTrack = 2
                });

                _overlays.Add(new OverlayClip
                {
                    IsText = true, Text = eyebrows[scene], Start = start + 0.35, Duration = 4.25,
                    X = 0.5, Y = 0.29, FontSize = 30, Color = template.Accent,
                    AnimationPreset = "Fade In · 03"
                });
                _overlays.Add(new OverlayClip
                {
                    IsText = true, Text = headings[scene], Start = start + 0.75, Duration = 3.9,
                    X = 0.5, Y = 0.49, FontSize = scene == 0 ? 82 : 70,
                    Color = scene == 2 ? template.Accent : "White", TextShape = "None",
                    AnimationPreset = motions[scene]
                });
                if (scene == 1)
                {
                    _overlays.Add(new OverlayClip
                    {
                        IsText = true, Text = "Replace these words in the timeline", Start = start + 1.4, Duration = 3.4,
                        X = 0.5, Y = 0.68, FontSize = 34, Color = "White", TextShape = "Caption box",
                        AnimationPreset = "Rise · 03"
                    });
                }
            }

            _playhead = 0;
            _undo.Clear(); _redo.Clear();
            AfterEdit();
            ShowFrame(false);
            LoadInspector();
            Status.Text = $"Created editable 15-second template · {template.Name}. Click any title on the timeline to change it.";
        }
        catch (Exception ex)
        {
            _timeline.Clear(); _overlays.Clear();
            Refresh(); ShowFrame(false);
            MessageBox.Show(this, "Could not create this video template.\n\n" + ex.Message, "Video template", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    string SampleTitle() => string.IsNullOrWhiteSpace(TemplateTitleText.Text) ? "Your title" : TemplateTitleText.Text.Trim();

    void BuildTemplateCards()
    {
        string category = TemplateCategoryBox.SelectedItem as string ?? "All";
        string? keep = TemplateShelfBox.SelectedItem as string;
        _templateSamples.Clear();
        TemplateCardList.Items.Clear();
        foreach (var template in TextTemplates.Where(t => category == "All" || t.Category == category))
            TemplateCardList.Items.Add(new ListBoxItem { Tag = template.Name, Content = MakeTemplateCard(template), ToolTip = $"{template.Name} · {template.Animation}" });
        var selected = TemplateCardList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == keep) ?? TemplateCardList.Items.Cast<ListBoxItem>().FirstOrDefault();
        if (selected != null) { TemplateCardList.SelectedItem = selected; TemplateCardList.ScrollIntoView(selected); }
    }

    FrameworkElement MakeTemplateCard(TextTemplateOption t)
    {
        const double W = 124, H = 76;
        var frame = new Grid
        {
            Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, H), 8, 8),
            Background = new LinearGradientBrush(Color.FromRgb(0x33, 0x38, 0x5C), Color.FromRgb(0x12, 0x13, 0x20), 50)
        };
        frame.Children.Add(new Border { Height = H * 0.36, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)) });
        var sample = new TextBlock
        {
            Text = SampleTitle(), FontSize = Math.Clamp(t.FontSize * 0.17, 9, 17), FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush(t.Color), TextTrimming = TextTrimming.CharacterEllipsis
        };
        _templateSamples.Add(sample);
        AttachHoverMotion(frame, sample, t.Animation);
        frame.Children.Add(new Border
        {
            Child = sample, Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(3), MaxWidth = W - 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Clamp(t.Y * H - 10, 3, H - 26), 0, 0),
            Background = t.Shape == "Caption box" ? new SolidColorBrush(Color.FromArgb(190, 12, 14, 18))
                       : t.Shape == "Highlight box" ? new SolidColorBrush(Color.FromArgb(210, 20, 120, 150)) : Brushes.Transparent
        });
        var card = new StackPanel { Width = W, Margin = new Thickness(3) };
        card.Children.Add(frame);
        card.Children.Add(new TextBlock { Text = t.Name, Margin = new Thickness(2, 4, 2, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0xE4, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis });
        card.Children.Add(new TextBlock { Text = "▸ " + t.Animation, Margin = new Thickness(2, 0, 2, 2), FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x92, 0xB4)), TextTrimming = TextTrimming.CharacterEllipsis });
        return card;
    }

    void BuildAnimationCards()
    {
        if (AnimationCardList == null || AnimationCategoryBox == null) return;
        string category = AnimationCategoryBox.SelectedItem as string ?? "All";
        string? keep = (AnimationCardList.SelectedItem as ListBoxItem)?.Tag as string;
        _animationSamples.Clear();
        AnimationCardList.Items.Clear();

        if (category == "All" || category == "None")
            AnimationCardList.Items.Add(new ListBoxItem { Tag = "None", Content = MakeAnimationCard("None", "Still title", "No motion") });
        foreach (var option in AnimationPresets.Where(p => category == "All" || p.Family == category))
            AnimationCardList.Items.Add(new ListBoxItem
            {
                Tag = option.Name,
                Content = MakeAnimationCard(option.Name, SampleTitle(), option.Family),
                ToolTip = $"{option.Family} · variation {option.Variation:00}"
            });

        var selected = AnimationCardList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == keep)
            ?? AnimationCardList.Items.Cast<ListBoxItem>().FirstOrDefault();
        if (selected != null) AnimationCardList.SelectedItem = selected;
    }

    FrameworkElement MakeAnimationCard(string name, string sampleText, string family)
    {
        const double width = 126, height = 70;
        var frame = new Grid
        {
            Width = width, Height = height, Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8),
            Background = new LinearGradientBrush(Color.FromRgb(0x36, 0x2E, 0x51), Color.FromRgb(0x14, 0x17, 0x22), 38)
        };
        frame.Children.Add(new Border { Height = 20, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)) });
        var sample = new TextBlock
        {
            Text = sampleText, MaxWidth = width - 16, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _animationSamples.Add(sample);
        AttachHoverMotion(frame, sample, name);
        frame.Children.Add(sample);

        var card = new StackPanel { Width = width, Margin = new Thickness(3) };
        card.Children.Add(frame);
        card.Children.Add(new TextBlock { Text = name, Margin = new Thickness(3, 4, 2, 0), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xD6, 0x9A)), TextTrimming = TextTrimming.CharacterEllipsis });
        card.Children.Add(new TextBlock { Text = family == "Still title" ? "PREMIUM STYLE" : "✦ " + family, Margin = new Thickness(3, 1, 2, 2), FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA8, 0xC0)), TextTrimming = TextTrimming.CharacterEllipsis });
        return card;
    }

    static void AttachHoverMotion(FrameworkElement frame, FrameworkElement sample, string presetName)
    {
        var option = AnimationPresets.FirstOrDefault(p => p.Name == presetName);
        if (option == null) return;
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var move = new TranslateTransform(0, 0);
        sample.RenderTransformOrigin = new Point(0.5, 0.5);
        sample.RenderTransform = new TransformGroup { Children = new TransformCollection { scale, rotate, move } };
        frame.MouseEnter += (_, _) =>
        {
            switch (option.Family)
            {
                case "Zoom In": case "Grow": scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1.12, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.55, 1.12, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Zoom Out": case "Shrink": scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.2, 0.82, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.2, 0.82, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Spin In": case "Spin Out": case "Twist": rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, option.Family == "Twist" ? 180 : 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Slide Left": move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, -18, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Slide Right": move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, 18, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Slide Up": case "Rise": move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, -12, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Slide Down": case "Drop": move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-12, 12, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Fade In": case "Wipe": sample.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(520)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Shake": move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-5, 5, TimeSpan.FromMilliseconds(65)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Pulse": case "Bubble Pop": case "Bounce": case "Pop": case "Elastic": scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1.18, TimeSpan.FromMilliseconds(380)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1.18, TimeSpan.FromMilliseconds(380)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                case "Float": case "Drift": move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-7, 7, TimeSpan.FromMilliseconds(760)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
                default: scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1.06, TimeSpan.FromMilliseconds(480)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); break;
            }
        };
        frame.MouseLeave += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            move.BeginAnimation(TranslateTransform.XProperty, null); move.BeginAnimation(TranslateTransform.YProperty, null);
            sample.BeginAnimation(UIElement.OpacityProperty, null);
            scale.ScaleX = scale.ScaleY = 1; rotate.Angle = 0; move.X = move.Y = 0; sample.Opacity = 1;
        };
    }

    void ApplyAnimationCard_Click(object sender, RoutedEventArgs e)
    {
        if (_selOv is not { IsText: true } text)
        {
            MessageBox.Show(this, "Select a text clip on the timeline, then apply an animation card.", "Select a text clip", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AnimationCardList.SelectedItem is not ListBoxItem { Tag: string name }) return;
        Pause(); Checkpoint(); text.AnimationPreset = name;
        _loading = true; AnimationBox.SelectedItem = name; _loading = false;
        AfterEdit();
        Status.Text = name == "None" ? "Removed text animation" : $"Applied text animation · {name}";
    }
}
