using System.IO;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.Services;

namespace VideoEditor;

/// <summary>Small dialog: type text, choose an installed Windows voice, create a WAV file.</summary>
public class TtsDialog : Window
{
    public string? ResultPath { get; private set; }

    readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, Margin = new Thickness(0, 0, 0, 8) };
    readonly ComboBox _voice = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly Slider _rate = new() { Minimum = -5, Maximum = 5, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 12) };
    readonly Button _ok = new() { Content = "Create voice", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right };

    public TtsDialog()
    {
        Title = "Text to speech";
        Width = 440; Height = 400;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowUi.Apply(this);

        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = "Text", Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(_text);
        sp.Children.Add(new TextBlock { Text = "Voice (installed Windows voices)", Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(_voice);
        sp.Children.Add(new TextBlock { Text = "Speed (-5 slow ... +5 fast)", Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(_rate);
        sp.Children.Add(_ok);
        Content = sp;

        try
        {
            using var synth = new SpeechSynthesizer();
            foreach (var v in synth.GetInstalledVoices()) _voice.Items.Add(v.VoiceInfo.Name);
        }
        catch { }
        if (_voice.Items.Count > 0) _voice.SelectedIndex = 0;

        _ok.Click += async (_, _) => await Create();
    }

    async Task Create()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0) { MessageBox.Show(this, "Type some text first."); return; }
        if (_voice.SelectedItem is not string voice) { MessageBox.Show(this, "No Windows voice is installed."); return; }

        int rate = (int)_rate.Value;
        _ok.IsEnabled = false;
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyEditorTTS");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"tts_{Guid.NewGuid():N}.wav");
            await Task.Run(() =>
            {
                using var synth = new SpeechSynthesizer();
                synth.SelectVoice(voice);
                synth.Rate = rate;
                synth.SetOutputToWaveFile(path);
                synth.Speak(text);
            });
            ResultPath = path;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Text to speech failed");
            _ok.IsEnabled = true;
        }
    }
}
