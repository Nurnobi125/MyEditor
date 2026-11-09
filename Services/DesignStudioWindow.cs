using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Models;

namespace VideoEditor.Services;

public enum DesignAction { Add, SetVideoBackground }

/// <summary>
/// Non-modal gallery of premium shapes and background designs. Cards can be dragged onto the timeline
/// (V1 = background scene, V2–V4 = overlay) or onto the preview (shape at that spot / video background).
/// </summary>
public sealed class DesignStudioWindow : Window
{
    const int PageSize = 48;

    readonly Func<int> _aspectIndex;
    readonly Button _shapesTab = new(), _backgroundsTab = new(), _animatedTab = new();
    readonly ComboBox _design = new(), _palette = new(), _style = new(), _motion = new(), _duration = new();
    readonly TextBox _search = new();
    readonly WrapPanel _cards = new() { Margin = new Thickness(2) };
    readonly ScrollViewer _scroll = new();
    readonly Button _more = new();
    readonly TextBlock _title = new(), _hint = new(), _count = new();
    readonly Button _add = new(), _setBackground = new();

    DesignKind _kind = DesignKind.Shape;
    List<DesignAsset> _results = new();
    int _shown;
    Border? _selectedCard;
    DesignAsset? _selected;
    Point _dragStart;
    DesignAsset? _dragCandidate;
    bool _suspend;

    static readonly double[] Durations = { 3, 5, 8, 10 };

    /// <summary>Raised for the "Add at playhead" / "Set as video background" buttons and double-click.</summary>
    public event Action<DesignDrag, DesignAction>? ActionRequested;

    public DesignStudioWindow(Func<int> aspectIndex)
    {
        _aspectIndex = aspectIndex;
        Title = "MyEditor · Shapes & Backgrounds";
        Width = 760; Height = 700; MinWidth = 600; MinHeight = 480;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brush("#15171D"); Foreground = Brush("#F1F3F7");
        WindowUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "PREMIUM SHAPES & BACKGROUNDS", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brush("#F5C542") });
        _hint.Text = "Drag a card onto the timeline (V1 = background scene, V2–V4 = overlay) or onto the preview. Double-click adds it at the playhead.";
        _hint.TextWrapping = TextWrapping.Wrap; _hint.Foreground = Brush("#A9AFBB"); _hint.Margin = new Thickness(0, 3, 0, 0);
        head.Children.Add(_hint);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);

        // ---- bottom bar ----
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right); bottom.Children.Add(buttons);
        StyleButton(_setBackground, "▣ Use as video background", "#5B3DF5"); _setBackground.Click += (_, _) => Raise(DesignAction.SetVideoBackground);
        StyleButton(_add, "＋ Add at playhead", "#149DB5"); _add.Margin = new Thickness(8, 0, 0, 0); _add.Click += (_, _) => Raise(DesignAction.Add);
        buttons.Children.Add(_setBackground); buttons.Children.Add(_add);
        _title.VerticalAlignment = VerticalAlignment.Center; _title.Foreground = Brush("#C9CFDA"); _title.TextTrimming = TextTrimming.CharacterEllipsis;
        bottom.Children.Add(_title);

        // ---- filters ----
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_shapesTab, "◆ Shapes", "#303642"); StyleButton(_backgroundsTab, "▦ Backgrounds", "#303642");
        StyleButton(_animatedTab, "▶ Animated", "#303642");
        _backgroundsTab.Margin = new Thickness(6, 0, 0, 0); _animatedTab.Margin = new Thickness(6, 0, 0, 0);
        _animatedTab.Click += (_, _) => SwitchKind(DesignKind.Animated);
        _shapesTab.Click += (_, _) => SwitchKind(DesignKind.Shape);
        _backgroundsTab.Click += (_, _) => SwitchKind(DesignKind.Background);
        tabs.Children.Add(_shapesTab); tabs.Children.Add(_backgroundsTab); tabs.Children.Add(_animatedTab);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var box in new[] { _design, _palette, _style, _motion, _duration })
        {
            box.Width = 138; box.Height = 30; box.Margin = new Thickness(0, 0, 8, 6);
            box.Background = Brush("#222630"); box.Foreground = Foreground;
        }
        _search.Width = 138; _search.Height = 30; _search.Margin = new Thickness(0, 0, 8, 6); _search.Padding = new Thickness(6, 4, 6, 4);
        _search.Background = Brush("#222630"); _search.Foreground = Foreground; _search.BorderBrush = Brush("#39404C"); _search.ToolTip = "Search by name";
        _motion.ToolTip = "Motion applied when the item is added";
        _duration.ToolTip = "How long it stays on the timeline";
        foreach (var d in Durations) _duration.Items.Add($"{d:0} seconds");
        _duration.SelectedIndex = 1;
        foreach (var m in DesignCatalog.MotionFamilies) _motion.Items.Add(m == "None" ? "Motion: none" : "Motion: " + m);
        _motion.SelectedIndex = 1;
        foreach (var element in new UIElement[] { _design, _palette, _style, _search, _motion, _duration }) filters.Children.Add(element);

        var top = new StackPanel();
        top.Children.Add(tabs); top.Children.Add(filters);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        _more.Content = "Show more"; _more.Margin = new Thickness(0, 8, 0, 4); _more.HorizontalAlignment = HorizontalAlignment.Center;
        StyleButton(_more, "Show more", "#303642"); _more.Click += (_, _) => ShowMore();
        _count.Foreground = Brush("#8E95A3"); _count.HorizontalAlignment = HorizontalAlignment.Center; _count.FontSize = 11;
        var pager = new StackPanel(); pager.Children.Add(_cards); pager.Children.Add(_more); pager.Children.Add(_count);
        _scroll.Content = pager; _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        root.Children.Add(new Border { Background = Brush("#1B1E25"), BorderBrush = Brush("#303541"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _scroll });

        foreach (var box in new[] { _design, _palette, _style })
            box.SelectionChanged += (_, _) => { if (!_suspend) Rebuild(); };
        _search.TextChanged += (_, _) => { if (!_suspend) Rebuild(); };

        SwitchKind(DesignKind.Shape);
    }

    // ------------------------------------------------------------ data
    DesignDrag? CurrentDrag(DesignAsset asset)
    {
        string family = DesignCatalog.MotionFamilies[Math.Clamp(_motion.SelectedIndex, 0, DesignCatalog.MotionFamilies.Length - 1)];
        double duration = Durations[Math.Clamp(_duration.SelectedIndex, 0, Durations.Length - 1)];
        // Backgrounds default to calm motion: a still image unless the user picked something explicitly.
        return new DesignDrag(asset, DesignCatalog.MotionPreset(family), duration);
    }

    void Raise(DesignAction action)
    {
        if (_selected == null)
        {
            MessageBox.Show(this, "Choose a card first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (action == DesignAction.SetVideoBackground && _selected.Kind != DesignKind.Background)
        {
            MessageBox.Show(this, "Only backgrounds can be used behind the video. Switch to the Backgrounds tab.", "Not a background", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ActionRequested?.Invoke(CurrentDrag(_selected)!, action);
    }

    void SwitchKind(DesignKind kind)
    {
        _kind = kind;
        _suspend = true;
        _shapesTab.Background = Brush(kind == DesignKind.Shape ? "#B33240" : "#303642");
        _backgroundsTab.Background = Brush(kind == DesignKind.Background ? "#B33240" : "#303642");
        _animatedTab.Background = Brush(kind == DesignKind.Animated ? "#B33240" : "#303642");
        _hint.Text = kind == DesignKind.Animated
            ? "Animated backgrounds loop seamlessly. The first time you add one it is rendered to a short MP4 (a few seconds), then it is instant."
            : "Drag a card onto the timeline (V1 = background scene, V2–V4 = overlay) or onto the preview. Double-click adds it at the playhead.";

        _design.Items.Clear(); _design.Items.Add(kind == DesignKind.Shape ? "All shapes" : "All backgrounds");
        foreach (var name in kind == DesignKind.Shape ? DesignCatalog.ShapeNames : DesignCatalog.BackgroundNames) _design.Items.Add(name);
        _design.SelectedIndex = 0;

        _palette.Items.Clear(); _palette.Items.Add("All colours");
        foreach (var name in DesignCatalog.PaletteNames) _palette.Items.Add(name);
        _palette.SelectedIndex = 0;

        _style.Items.Clear(); _style.Items.Add(kind == DesignKind.Animated ? "All motions" : "All styles");
        foreach (var name in kind == DesignKind.Animated ? DesignCatalog.AnimatedStyles : DesignCatalog.ShapeStyles) _style.Items.Add(name);
        _style.SelectedIndex = 0;
        _style.Visibility = kind == DesignKind.Background ? Visibility.Collapsed : Visibility.Visible;
        _setBackground.Visibility = kind == DesignKind.Background ? Visibility.Visible : Visibility.Collapsed;
        _motion.SelectedIndex = kind == DesignKind.Shape ? 1 : 0;   // shapes: Pop, backgrounds: none
        _search.Text = "";
        _suspend = false;
        Rebuild();
    }

    void Rebuild()
    {
        string design = _design.SelectedIndex > 0 ? _design.SelectedItem as string ?? "" : "";
        string palette = _palette.SelectedIndex > 0 ? _palette.SelectedItem as string ?? "" : "";
        string style = _kind != DesignKind.Background && _style.SelectedIndex > 0 ? _style.SelectedItem as string ?? "" : "";
        string search = _search.Text.Trim();
        var source = _kind == DesignKind.Shape ? DesignCatalog.Shapes : _kind == DesignKind.Animated ? DesignCatalog.Animated : DesignCatalog.Backgrounds;
        _results = source.Where(a => (design.Length == 0 || a.Design == design) && (palette.Length == 0 || a.Palette == palette) &&
            (style.Length == 0 || a.Style == style) && (search.Length == 0 || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        _cards.Children.Clear();
        _shown = 0; _selectedCard = null; _selected = null;
        _title.Text = "Choose a design";
        ShowMore();
        _scroll.ScrollToTop();
    }

    void ShowMore()
    {
        int end = Math.Min(_results.Count, _shown + PageSize);
        for (int i = _shown; i < end; i++) _cards.Children.Add(MakeCard(_results[i]));
        _shown = end;
        _more.Visibility = _shown < _results.Count ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = _results.Count == 0 ? "No designs match this filter" : $"Showing {_shown:N0} of {_results.Count:N0}";
    }

    // ------------------------------------------------------------ cards + drag source
    FrameworkElement MakeCard(DesignAsset asset)
    {
        bool shape = asset.Kind == DesignKind.Shape;
        var image = new Image { Source = DesignCatalog.Thumbnail(asset, _aspectIndex()), Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        double w = shape ? 112 : 176, h = shape ? 112 : _aspectIndex() == 1 ? 200 : 100;
        var tile = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(6), Child = image,
            Background = shape ? Brush("#262A35") : Brushes.Transparent
        };
        var label = new TextBlock { Text = shape ? asset.Palette + " · " + asset.Style : asset.Kind == DesignKind.Animated ? asset.Palette + " · ▶ " + asset.Style : asset.Palette, FontSize = 10.5, Foreground = Brush("#B8BECB"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 3, 2, 0), MaxWidth = w };
        var stack = new StackPanel(); stack.Children.Add(tile); stack.Children.Add(label);
        var card = new Border
        {
            Child = stack, Margin = new Thickness(4), Padding = new Thickness(4), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Brush("#20232B"),
            Tag = asset, Cursor = Cursors.Hand, ToolTip = asset.Name
        };
        card.MouseLeftButtonDown += Card_MouseDown;
        card.MouseMove += Card_MouseMove;
        card.MouseLeftButtonUp += (_, _) => _dragCandidate = null;
        return card;
    }

    void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DesignAsset asset } card) return;
        Select(card, asset);
        _dragStart = e.GetPosition(this);
        _dragCandidate = asset;
        if (e.ClickCount == 2) { _dragCandidate = null; Raise(DesignAction.Add); }
    }

    void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed || sender is not Border card) return;
        Point p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var asset = _dragCandidate; _dragCandidate = null;
        DragDrop.DoDragDrop(card, new DataObject(typeof(DesignDrag), CurrentDrag(asset)!), DragDropEffects.Copy);
    }

    void Select(Border card, DesignAsset asset)
    {
        if (_selectedCard != null) _selectedCard.BorderBrush = Brushes.Transparent;
        _selectedCard = card; _selected = asset;
        card.BorderBrush = Brush("#F5C542");
        _title.Text = asset.Name;
    }

    static void StyleButton(Button button, string text, string hex)
    {
        button.Content = text; button.Padding = new Thickness(12, 6, 12, 6); button.Foreground = Brushes.White;
        button.Background = Brush(hex); button.BorderThickness = new Thickness(0); button.FontWeight = FontWeights.SemiBold;
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex)!);
}
