using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoEditor.Models;

namespace VideoEditor.Services;

/// <summary>Virtualized local sticker gallery with category, search and motion preview cards.</summary>
public sealed class StickerBrowserWindow : Window
{
    readonly ComboBox _category = new();
    readonly TextBox _search = new();
    readonly ListBox _list = new();
    readonly Border _artPreview = new();
    readonly TextBlock _title = new();
    readonly TextBlock _details = new();
    readonly TextBlock _count = new();

    public StickerPreset? SelectedSticker => _list.SelectedItem as StickerPreset;

    public StickerBrowserWindow()
    {
        Title = "MyEditor · Animated Stickers";
        Width = 920; Height = 650; MinWidth = 720; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brush("#15171D"); Foreground = Brush("#F1F3F7");
        WindowUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock { Text = "ANIMATED STICKERS & PREMIUM EMOJI", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brush("#D3C1FF") });
        head.Children.Add(new TextBlock { Text = "Pick a sticker or emoji and motion style, then add it to the timeline at the playhead.", Margin = new Thickness(0, 3, 0, 0), Foreground = Brush("#A9AFBB") });
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _count.Text = $"{StickerCatalog.All.Count:N0} animated combinations · CPU-friendly still artwork with timeline motion";
        _count.Foreground = Brush("#A0A8B5"); _count.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_count);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var close = MakeButton("Close", "#303642"); close.Click += (_, _) => { DialogResult = false; Close(); };
        var add = MakeButton("＋ Add sticker", "#149DB5"); add.Margin = new Thickness(8, 0, 0, 0); add.IsDefault = true;
        add.Click += (_, _) =>
        {
            if (SelectedSticker == null) { MessageBox.Show(this, "Choose a sticker first.", "No sticker selected", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            DialogResult = true; Close();
        };
        buttons.Children.Add(close); buttons.Children.Add(add);

        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); root.Children.Add(body);
        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) }; Grid.SetColumn(left, 0); body.Children.Add(left);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(filters, Dock.Top); left.Children.Add(filters);
        _category.Width = 150; _category.Height = 32; _category.Margin = new Thickness(0, 0, 8, 0); _category.Background = Brush("#222630"); _category.Foreground = Foreground;
        _category.Items.Add("All categories");
        foreach (var categoryName in StickerCatalog.Categories) _category.Items.Add(categoryName);
        _category.SelectedIndex = 0; _category.SelectionChanged += (_, _) => RefreshList(); filters.Children.Add(_category);
        _search.Width = 230; _search.Height = 32; _search.Padding = new Thickness(8, 5, 8, 5); _search.Background = Brush("#222630"); _search.Foreground = Foreground; _search.BorderBrush = Brush("#39404C"); _search.ToolTip = "Search sticker names and motion";
        _search.TextChanged += (_, _) => RefreshList(); filters.Children.Add(_search);

        _list.Background = Brush("#1B1E25"); _list.Foreground = Foreground; _list.BorderBrush = Brush("#303541"); _list.SelectionChanged += (_, _) => UpdatePreview();
        VirtualizingStackPanel.SetIsVirtualizing(_list, true); VirtualizingStackPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(_list, true);
        var template = new DataTemplate(typeof(StickerPreset));
        var row = new FrameworkElementFactory(typeof(DockPanel)); row.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 3, 6, 3));
        var glyph = new FrameworkElementFactory(typeof(TextBlock)); glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(StickerPreset.Glyph)));
        glyph.SetValue(TextBlock.FontSizeProperty, 25.0); glyph.SetValue(TextBlock.WidthProperty, 44.0); glyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); glyph.SetValue(TextBlock.ForegroundProperty, Brush("#7DDCF0"));
        row.AppendChild(glyph);
        var labels = new FrameworkElementFactory(typeof(StackPanel)); labels.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        var nameLabel = new FrameworkElementFactory(typeof(TextBlock)); nameLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(StickerPreset.Name))); nameLabel.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); nameLabel.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var category = new FrameworkElementFactory(typeof(TextBlock)); category.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(StickerPreset.Category))); category.SetValue(TextBlock.FontSizeProperty, 10.0); category.SetValue(TextBlock.ForegroundProperty, Brush("#9AA3B1"));
        labels.AppendChild(nameLabel); labels.AppendChild(category); row.AppendChild(labels); template.VisualTree = row; _list.ItemTemplate = template;
        left.Children.Add(_list);

        var right = new Border { Background = Brush("#1D2028"), BorderBrush = Brush("#303541"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14) };
        Grid.SetColumn(right, 1); body.Children.Add(right);
        var preview = new StackPanel(); right.Child = preview;
        preview.Children.Add(new TextBlock { Text = "STICKER PREVIEW", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brush("#AEB5C2") });
        _artPreview.Height = 200; _artPreview.Margin = new Thickness(0, 12, 0, 10); _artPreview.CornerRadius = new CornerRadius(8); _artPreview.Background = Brush("#282C36");
        _artPreview.Child = new TextBlock { Text = "✦", FontSize = 90, Foreground = Brush("#7DDCF0"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(_artPreview);
        _title.Text = "Choose a sticker"; _title.FontSize = 16; _title.FontWeight = FontWeights.SemiBold; preview.Children.Add(_title);
        _details.Text = "The selected sticker will be added as an overlay at the current playhead."; _details.Margin = new Thickness(0, 6, 0, 0); _details.Foreground = Brush("#A9AFBB"); _details.TextWrapping = TextWrapping.Wrap; preview.Children.Add(_details);
        preview.Children.Add(new TextBlock { Text = "Change its position, size, opacity and duration in the Clip Inspector. Drag it on the timeline to move it.", Margin = new Thickness(0, 16, 0, 0), FontSize = 11, Foreground = Brush("#89919F"), TextWrapping = TextWrapping.Wrap });

        RefreshList();
    }

    void RefreshList()
    {
        string category = _category.SelectedItem?.ToString() ?? "All categories";
        string search = _search.Text.Trim();
        _list.ItemsSource = StickerCatalog.All.Where(s => (category == "All categories" || s.Category == category) &&
            (string.IsNullOrWhiteSpace(search) || s.Name.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        _count.Text = $"{_list.Items.Count:N0} stickers in this view · all animation runs on the timeline without GPU";
    }

    void UpdatePreview()
    {
        if (SelectedSticker is not { } sticker) return;
        var color = (Color)ColorConverter.ConvertFromString(sticker.Color)!;
        _artPreview.Background = new LinearGradientBrush(Color.FromArgb(55, color.R, color.G, color.B), BrushColor("#20232B"), 35);
        _artPreview.Child = new TextBlock { Text = sticker.Glyph, FontSize = 100, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _title.Text = sticker.Name;
        _details.Text = $"{sticker.Category} · {sticker.AnimationPreset} · ready to add at the playhead";
    }

    static Button MakeButton(string text, string hex) => new() { Content = text, Padding = new Thickness(12, 7, 12, 7), Background = Brush(hex), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold };
    static SolidColorBrush Brush(string hex) => new(BrushColor(hex));
    static Color BrushColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
