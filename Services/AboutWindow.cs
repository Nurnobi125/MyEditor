using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoEditor.Services;

/// <summary>Product information opened from Help > About.</summary>
public sealed class AboutWindow : Window
{
    static SolidColorBrush ColorBrush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    public AboutWindow()
    {
        Title = "About MyEditor";
        Width = 510;
        Height = 500;
        MinWidth = 440;
        MinHeight = 440;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ColorBrush("#15171D");
        Foreground = ColorBrush("#F1F3F7");
        WindowUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var notices = new Button
        {
            Content = "Third-party notices",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ColorBrush("#292D38"),
            Foreground = Foreground,
            BorderBrush = ColorBrush("#414756")
        };
        notices.Click += (_, _) =>
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
                MessageBox.Show(this, "The third-party notices file was not found beside the application.", "Notices unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        footer.Children.Add(notices);

        var close = new Button
        {
            Content = "Close",
            Padding = new Thickness(18, 7, 18, 7),
            Background = ColorBrush("#6B55D9"),
            Foreground = Brushes.White,
            BorderBrush = ColorBrush("#6B55D9"),
            IsDefault = true
        };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);

        var content = new StackPanel();
        root.Children.Add(content);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(brand);
        try
        {
            var logo = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")),
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 14, 0)
            };
            brand.Children.Add(logo);
        }
        catch { /* The About page remains usable if a custom build omits the logo resource. */ }

        var nameBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameBlock.Children.Add(new TextBlock { Text = "MyEditor", FontSize = 25, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        nameBlock.Children.Add(new TextBlock { Text = "Video Studio", FontSize = 13, Foreground = ColorBrush("#AEB5C4"), Margin = new Thickness(1, 2, 0, 0) });
        brand.Children.Add(nameBlock);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        string versionText = version is null ? "3.3.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        var versionPill = new Border
        {
            Background = ColorBrush("#25283A"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 20, 0, 14),
            Child = new TextBlock { Text = $"VERSION  {versionText}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ColorBrush("#C8B7FF") }
        };
        content.Children.Add(versionPill);

        content.Children.Add(new TextBlock
        {
            Text = "A lightweight workspace for recording tutorials and editing video.",
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ColorBrush("#E3E6ED"),
            Margin = new Thickness(0, 0, 0, 16)
        });

        var features = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        content.Children.Add(features);
        AddFeature(features, "Screen recording", "Capture your screen and bring recordings into the editing timeline.");
        AddFeature(features, "Multitrack editing", "Arrange video, overlays, titles, stickers and audio on separate tracks.");
        AddFeature(features, "Tutorial tools", "Add editable zoom moments, cursor highlights, captions and text.");
        AddFeature(features, "Export controls", "Choose video size, frame rate, quality and a compatible format.");

        content.Children.Add(new TextBlock
        {
            Text = "Made for Windows · MyEditor Video Studio",
            FontSize = 11,
            Foreground = ColorBrush("#858D9D"),
            Margin = new Thickness(0, 10, 0, 0)
        });
    }

    static void AddFeature(Panel panel, string title, string description)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(row);
        row.Children.Add(new TextBlock { Text = "●", FontSize = 9, Foreground = ColorBrush("#31C7D5"), Margin = new Thickness(2, 5, 10, 0) });
        var text = new StackPanel();
        row.Children.Add(text);
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ColorBrush("#E9EBF2") });
        text.Children.Add(new TextBlock { Text = description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush("#A9AFBB"), Margin = new Thickness(0, 2, 0, 0) });
    }
}
