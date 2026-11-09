using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoEditor.Models;

namespace VideoEditor.Services;

/// <summary>Searchable, category-based effect and transition browser with lightweight visual previews.</summary>
public sealed class EffectsBrowserWindow : Window
{
    static readonly string[] Transitions =
    {
        "None", "Fade", "Dissolve", "Fade to black", "Fade to white", "Wipe left", "Wipe right", "Wipe up", "Wipe down",
        "Slide left", "Slide right", "Slide up", "Slide down", "Smooth left", "Smooth right", "Smooth up", "Smooth down",
        "Circle open", "Circle close", "Circle crop", "Pixelize", "Radial", "Zoom in", "Diagonal top-left", "Diagonal top-right",
        "Diagonal bottom-left", "Diagonal bottom-right", "Vertical open", "Vertical close", "Horizontal open", "Horizontal close"
    };
    readonly ComboBox _category = new();
    readonly TextBox _search = new();
    readonly WrapPanel _effectCards = new();
    readonly WrapPanel _transitionCards = new();
    WrapPanel _cards;
    readonly TextBlock _previewTitle = new();
    readonly TextBlock _previewDescription = new();
    readonly Border _previewSurface = new();
    readonly TabControl _tabs = new();
    string? _effect;
    string? _transition;
    bool _refreshing;

    public string? SelectedEffect => _effect;
    public string? SelectedTransition => _transition;

    public EffectsBrowserWindow()
    {
        _cards = _effectCards;
        Title = "MyEditor · Effects & Transitions";
        Width = 980; Height = 700; MinWidth = 760; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brush("#15171D"); Foreground = Brush("#F1F3F7");
        WindowUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "EFFECTS & TRANSITIONS", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brush("#D3C1FF") });
        heading.Children.Add(new TextBlock { Text = "Browse visual presets by category, preview a style, then apply it to the selected clip.", Margin = new Thickness(0, 3, 0, 0), Foreground = Brush("#A9AFBB") });
        header.Children.Add(heading);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = Button("Close", "#303642"); cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var apply = Button("Apply to selected clip", "#149DB5"); apply.Margin = new Thickness(8, 0, 0, 0); apply.IsDefault = true;
        apply.Click += (_, _) =>
        {
            if (_tabs.SelectedIndex == 0 && _effect == null || _tabs.SelectedIndex == 1 && _transition == null)
            {
                MessageBox.Show(this, "Choose an effect or transition card first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true; Close();
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); header.Children.Add(actions);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        root.Children.Add(body);

        var browser = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(browser, 0); body.Children.Add(browser);
        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(controls, Dock.Top); browser.Children.Add(controls);
        _search.Width = 220; _search.Height = 32; _search.Margin = new Thickness(0, 0, 8, 0); _search.Padding = new Thickness(8, 5, 8, 5);
        _search.Background = Brush("#222630"); _search.Foreground = Foreground; _search.BorderBrush = Brush("#39404C"); _search.ToolTip = "Search effect names";
        _search.TextChanged += (_, _) => RefreshCards(); controls.Children.Add(_search);
        _category.Width = 190; _category.Height = 32; _category.Background = Brush("#222630"); _category.Foreground = Foreground;
        _category.SelectionChanged += (_, _) => RefreshCards(); controls.Children.Add(_category);

        _tabs.SelectionChanged += (_, _) => { SetCategories(); RefreshCards(); };
        DockPanel.SetDock(_tabs, Dock.Top); browser.Children.Add(_tabs);
        _tabs.Items.Add(new TabItem { Header = $"Effects  ·  {EffectCatalog.All.Count}", Content = new Border() });
        _tabs.Items.Add(new TabItem { Header = $"Transitions  ·  {Transitions.Length - 1}", Content = new Border() });
        var preview = new Border { Background = Brush("#1D2028"), BorderBrush = Brush("#303541"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14) };
        Grid.SetColumn(preview, 1); body.Children.Add(preview);
        var pv = new StackPanel(); preview.Child = pv;
        pv.Children.Add(new TextBlock { Text = "PREVIEW", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brush("#AEB5C2") });
        _previewSurface.Height = 190; _previewSurface.Margin = new Thickness(0, 12, 0, 10); _previewSurface.CornerRadius = new CornerRadius(7);
        _previewSurface.Background = new LinearGradientBrush(ColorFrom("#167B9A"), ColorFrom("#62459C"), 35);
        pv.Children.Add(_previewSurface);
        _previewTitle.Text = "Choose a preset"; _previewTitle.FontSize = 16; _previewTitle.FontWeight = FontWeights.SemiBold;
        pv.Children.Add(_previewTitle);
        _previewDescription.Text = "Select a card to see its category and application details."; _previewDescription.Margin = new Thickness(0, 6, 0, 0); _previewDescription.Foreground = Brush("#A9AFBB"); _previewDescription.TextWrapping = TextWrapping.Wrap;
        pv.Children.Add(_previewDescription);
        pv.Children.Add(new TextBlock { Text = "Preview cards are lightweight samples. Final effect is rendered when you export.", Margin = new Thickness(0, 16, 0, 0), Foreground = Brush("#89919F"), TextWrapping = TextWrapping.Wrap, FontSize = 11 });

        _tabs.SelectedIndex = 0; SetCategories(); RefreshCards();
    }

    void SetCategories()
    {
        if (_tabs.SelectedItem is TabItem tab)
        {
            _cards = _tabs.SelectedIndex == 0 ? _effectCards : _transitionCards;
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            scroll.Content = _cards;
            tab.Content = scroll;
        }
        _refreshing = true;
        _category.ItemsSource = _tabs.SelectedIndex == 0
            ? new[] { "All categories", "Cinematic", "Film & Retro", "Color & Mood", "Monochrome", "Soft & Dreamy" }
            : new[] { "All categories", "Basic fades", "Wipes", "Motion", "Shapes", "Stylized" };
        _category.SelectedIndex = 0;
        _refreshing = false;
    }

    void RefreshCards()
    {
        if (_refreshing || _category.SelectedItem == null) return;
        _cards.Children.Clear();
        if (_tabs.SelectedIndex == 0)
        {
            var query = _search.Text.Trim();
            var effects = EffectCatalog.All.Where(e => MatchesCategory(e.Family, _category.SelectedItem.ToString()!, false))
                .Where(e => string.IsNullOrWhiteSpace(query) || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            foreach (var effect in effects) _cards.Children.Add(CreateCard(effect.Name, effect.Family, effect.Strength));
        }
        else
        {
            string query = _search.Text.Trim();
            var transitions = Transitions.Where(t => MatchesCategory(t, _category.SelectedItem.ToString()!, true))
                .Where(t => string.IsNullOrWhiteSpace(query) || t.Contains(query, StringComparison.OrdinalIgnoreCase));
            foreach (var transition in transitions) _cards.Children.Add(CreateCard(transition, TransitionCategory(transition), 3));
        }
    }

    Border CreateCard(string name, string family, int intensity)
    {
        var color = FamilyColor(family);
        var card = new Border { Width = 154, Height = 124, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8), Background = Brush("#222630"), BorderBrush = Brush("#373D49"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Cursor = System.Windows.Input.Cursors.Hand, Tag = name };
        var stack = new StackPanel();
        var thumb = new Border { Height = 68, CornerRadius = new CornerRadius(5), Background = new LinearGradientBrush(color, Blend(color, ColorFrom("#111318"), 0.62), 25) };
        thumb.Child = new TextBlock { Text = _tabs.SelectedIndex == 0 ? "Aa" : "A  ›  B", FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = 0.9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(thumb);
        stack.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 7, 0, 0), FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = Brush("#EDF0F6"), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = family, FontSize = 9, Foreground = Brush("#A5ACB9") }); card.Child = stack;
        card.MouseLeftButtonUp += (_, _) => SelectCard(card, name, family, color, intensity);
        return card;
    }

    void SelectCard(Border card, string name, string family, Color color, int strength)
    {
        foreach (var item in _cards.Children.OfType<Border>()) item.BorderBrush = Brush("#373D49");
        card.BorderBrush = Brush("#36C7E3");
        if (_tabs.SelectedIndex == 0) { _effect = name; _transition = null; }
        else { _transition = name; _effect = null; }
        _previewTitle.Text = name;
        _previewDescription.Text = _tabs.SelectedIndex == 0 ? $"{family} look · strength {strength}/5 · lightweight color processing" : $"{family} transition · applied at the start of the selected clip";
        _previewSurface.Background = new LinearGradientBrush(color, Blend(color, ColorFrom("#111318"), 0.62), 25);
        _previewSurface.Child = new TextBlock { Text = _tabs.SelectedIndex == 0 ? "COLOR LOOK" : "A   ➜   B", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    static bool MatchesCategory(string value, string category, bool transition)
    {
        if (category == "All categories") return true;
        if (transition)
        {
            string family = TransitionCategory(value);
            return category switch { "Basic fades" => family == "Basic fades", "Wipes" => family == "Wipes", "Motion" => family == "Motion", "Shapes" => family == "Shapes", _ => family == "Stylized" };
        }
        return category switch
        {
            "Cinematic" => value.Contains("Cinematic") || value.Contains("Steel") || value.Contains("Chrome") || value.Contains("Bypass"),
            "Film & Retro" => value.Contains("Film") || value.Contains("Retro") || value.Contains("Sepia") || value.Contains("Summer") || value.Contains("Matte"),
            "Color & Mood" => value.Contains("Vivid") || value.Contains("Golden") || value.Contains("Arctic") || value.Contains("Night") || value.Contains("Sunset") || value.Contains("Forest") || value.Contains("Ocean"),
            "Monochrome" => value.Contains("Noir") || value.Contains("Mono"),
            _ => value.Contains("Soft") || value.Contains("Dreamy") || value.Contains("Rose")
        };
    }

    static string TransitionCategory(string name) => name switch
    {
        "None" or "Fade" or "Dissolve" or "Fade to black" or "Fade to white" => "Basic fades",
        var n when n.StartsWith("Wipe", StringComparison.Ordinal) || n.StartsWith("Diagonal", StringComparison.Ordinal) => "Wipes",
        var n when n.StartsWith("Circle", StringComparison.Ordinal) || n.StartsWith("Vertical", StringComparison.Ordinal) || n.StartsWith("Horizontal", StringComparison.Ordinal) => "Shapes",
        "Pixelize" or "Radial" or "Zoom in" => "Stylized",
        _ => "Motion"
    };

    static Button Button(string text, string color) => new() { Content = text, Padding = new Thickness(12, 7, 12, 7), Background = Brush(color), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold };
    static SolidColorBrush Brush(string hex) => new(ColorFrom(hex));
    static Color ColorFrom(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    static Color FamilyColor(string family)
    {
        int hash = Math.Abs(family.GetHashCode(StringComparison.Ordinal));
        return Color.FromRgb((byte)(65 + hash % 135), (byte)(70 + hash / 7 % 130), (byte)(100 + hash / 17 % 130));
    }
    static Color Blend(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R * (1 - amount) + b.R * amount), (byte)(a.G * (1 - amount) + b.G * amount), (byte)(a.B * (1 - amount) + b.B * amount));
}
