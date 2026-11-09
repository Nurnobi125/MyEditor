using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VideoEditor.Services;

/// <summary>Small settings sheet for reusable output framing/background choices.</summary>
public sealed class AdvancedVideoSettingsWindow : Window
{
    readonly ComboBox _mode = new();
    readonly ComboBox _palette = new();
    readonly TextBox _hex = new();
    readonly TextBox _image = new();

    public string BackgroundMode => _mode.SelectedItem as string ?? "Black bars";
    public string BackgroundColor => NormalizeHex(_hex.Text);
    public string BackgroundImagePath => _image.Text.Trim();

    public AdvancedVideoSettingsWindow(string mode, string color, string imagePath)
    {
        Title = "MyEditor · Advanced Video Lab";
        Width = 590; Height = Math.Min(650, SystemParameters.WorkArea.Height * 0.9);
        MinWidth = 500; MinHeight = Math.Min(500, SystemParameters.WorkArea.Height * 0.75);
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x20));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF2));
        WindowUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        title.Children.Add(new TextBlock { Text = "ADVANCED VIDEO LAB", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xB8, 0xFF)) });
        title.Children.Add(new TextBlock { Text = "Customize how video fits the selected frame and choose an editor-friendly recording profile.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xAD, 0xBA)), Margin = new Thickness(0, 5, 0, 0) });
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(4) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var apply = new Button { Content = "Apply settings", MinWidth = 125, Margin = new Thickness(4), Background = new SolidColorBrush(Color.FromRgb(0x63, 0x46, 0xA3)), FontWeight = FontWeights.SemiBold, IsDefault = true };
        apply.Click += (_, _) =>
        {
            if (BackgroundMode == "Custom image" && !File.Exists(BackgroundImagePath))
            {
                MessageBox.Show(this, "Choose an existing background image file first.", "Background image missing", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!IsHex(_hex.Text))
            {
                MessageBox.Show(this, "Enter a color in #RRGGBB format, or choose a color preset.", "Invalid color", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true; Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var body = new StackPanel();
        var bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(bodyScroll);
        AddLabel(body, "Background fill");
        _mode.ItemsSource = new[] { "Black bars", "Soft blur fill", "Solid color", "Custom image" };
        _mode.MaxDropDownHeight = 260;
        _mode.SelectedItem = mode;
        if (_mode.SelectedItem == null) _mode.SelectedIndex = 0;
        body.Children.Add(_mode);
        AddLabel(body, "Quick color presets");
        _palette.ItemsSource = new KeyValuePair<string, string>[]
        {
            new("Midnight", "#151820"), new("Studio navy", "#17243A"), new("Soft violet", "#33234A"),
            new("Deep teal", "#12363A"), new("Warm sand", "#77644E"), new("Cream", "#E8DFD0"),
            new("Slate", "#414954"), new("White", "#FFFFFF")
        };
        _palette.DisplayMemberPath = "Key";
        _palette.MaxDropDownHeight = 260;
        _palette.SelectionChanged += (_, _) =>
        {
            if (_palette.SelectedItem is KeyValuePair<string, string> pair) _hex.Text = pair.Value;
        };
        body.Children.Add(_palette);
        AddLabel(body, "Custom background color · #RRGGBB");
        _hex.Text = IsHex(color) ? color : "#151820";
        body.Children.Add(_hex);
        AddLabel(body, "Background image (used in Custom image mode)");
        var fileRow = new DockPanel();
        var browse = new Button { Content = "Browse…", MinWidth = 90, Margin = new Thickness(4, 2, 0, 6), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x33)), Foreground = Brushes.White };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Choose a background image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) _image.Text = dialog.FileName;
        };
        DockPanel.SetDock(browse, Dock.Right); fileRow.Children.Add(browse);
        _image.Text = imagePath;
        fileRow.Children.Add(_image); body.Children.Add(fileRow);
        AddLabel(body, "Recording profile");
        body.Children.Add(new TextBlock
        {
            Text = "Use Settings → Quick setup for one-click Lite / Recommended / Higher quality profiles. Extra Lite keeps CPU and memory use low; Smooth 30 fps and 60 fps use higher detail and more CPU. A 2 GB PC cannot guarantee native-resolution 60 fps without dropped frames.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xAD, 0xBA)), Margin = new Thickness(0, 4, 0, 0)
        });

        foreach (var control in new Control[] { _mode, _palette, _hex, _image, cancel, apply })
        {
            control.Foreground = control is Button b && b == apply ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF2));
            control.Background ??= new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x33));
            control.Margin = control.Margin == default ? new Thickness(0, 2, 0, 6) : control.Margin;
            control.Padding = new Thickness(8, 5, 8, 5);
            if (control is Button) control.BorderThickness = new Thickness(0);
        }
        var dropdownStyle = new Style(typeof(ComboBoxItem));
        dropdownStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF2))));
        dropdownStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x33))));
        dropdownStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        dropdownStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _mode.ItemContainerStyle = dropdownStyle;
        _palette.ItemContainerStyle = dropdownStyle;
        _hex.Foreground = _image.Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF2));
        _hex.Background = _image.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x33));
    }

    static void AddLabel(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 9, 0, 2), Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCB, 0xD4)) });
    static bool IsHex(string? value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit);
    static string NormalizeHex(string? value) => IsHex(value) ? value!.ToUpperInvariant() : "#151820";
}
