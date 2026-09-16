using System.Windows.Controls;
using PodGate.Core;
using PodGate.Core.Audio;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PodGate.App.Ui;

/// <summary>Which audio devices one mode switches to. Opened from the mode's "Audio devices" link.</summary>
public partial class AudioWindow : DarkWindow
{
    public enum Mode
    {
        Connect,
        Music,
        Disconnect,
    }

    private readonly Mode _mode;
    private bool _loading = true;

    public AudioWindow(Mode mode)
    {
        InitializeComponent();
        _mode = mode;

        (HeadingText.Text, ExplanationText.Text) = mode switch
        {
            Mode.Music => ("Connect for music",
                "What PodGate switches to when you connect for music. The AirPods' microphone is off in this mode, so calls use whatever you pick here."),
            Mode.Disconnect => ("Disconnect",
                "What PodGate switches back to when the AirPods are disconnected. “Previous device” is whatever was default before they were connected."),
            _ => ("Connect", "What PodGate switches to when you connect with music and calls."),
        };

        UserSettings settings = UserSettings.Load();
        ModeAudio audio = Current(settings);
        Fill(OutputBox, AudioFlow.Render, audio.Output);
        Fill(MicrophoneBox, AudioFlow.Capture, audio.Microphone);
        Fill(CallMicrophoneBox, AudioFlow.Capture, audio.CallMicrophone);
        _loading = false;

        OutputBox.SelectionChanged += (_, _) => Save(audio => audio.Output = Chosen(OutputBox));
        MicrophoneBox.SelectionChanged += (_, _) => Save(audio => audio.Microphone = Chosen(MicrophoneBox));
        CallMicrophoneBox.SelectionChanged += (_, _) => Save(audio => audio.CallMicrophone = Chosen(CallMicrophoneBox));
        DoneButton.Click += (_, _) => Close();
    }

    private ModeAudio Current(UserSettings settings) => _mode switch
    {
        Mode.Music => settings.MusicAudio,
        Mode.Disconnect => settings.DisconnectAudio,
        _ => settings.ConnectAudio,
    };

    private void Save(Action<ModeAudio> change)
    {
        if (_loading) return;
        UserSettings settings = UserSettings.Load();
        change(Current(settings));
        settings.Save();
    }

    /// <summary>
    /// The choices for one picker: leave it alone, the AirPods, what was default before (disconnect only),
    /// then every device Windows currently has.
    /// </summary>
    private void Fill(ComboBox box, AudioFlow flow, AudioTarget chosen)
    {
        var items = new List<AudioTarget> { AudioTarget.Of(AudioTarget.Unchanged) };
        if (_mode == Mode.Disconnect) items.Add(AudioTarget.Of(AudioTarget.Previous));
        else items.Add(AudioTarget.Of(AudioTarget.AirPods));

        foreach (AudioEndpoint endpoint in AudioEndpoints.All(flow))
        {
            items.Add(AudioTarget.Of(endpoint.EndpointId, endpoint.FriendlyName ?? endpoint.EndpointId));
        }

        // A device that is not plugged in right now still belongs in the list: it is the saved choice.
        if (chosen.Kind == AudioTarget.Device && items.All(item => item.EndpointId != chosen.EndpointId)) items.Add(chosen);

        box.ItemsSource = items.Select(item => item.Describe()).ToList();
        box.Tag = items;
        box.SelectedIndex = Math.Max(0, items.FindIndex(item =>
            item.Kind == chosen.Kind && (item.Kind != AudioTarget.Device || item.EndpointId == chosen.EndpointId)));
    }

    private static AudioTarget Chosen(ComboBox box) =>
        box.Tag is List<AudioTarget> items && box.SelectedIndex >= 0 && box.SelectedIndex < items.Count
            ? items[box.SelectedIndex]
            : AudioTarget.Of(AudioTarget.Unchanged);
}
