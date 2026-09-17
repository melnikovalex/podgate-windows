using System.Text.Json;
using System.Text.Json.Serialization;

namespace PodGate.Core;

/// <summary>
/// What each user decides for themselves, without an administrator prompt: shortcuts, autostart, whether
/// setup has run. Lives in %LOCALAPPDATA%\PodGate\settings.json. Hotkeys fall back to config.json's until
/// the user changes one.
/// </summary>
public sealed class UserSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool SetupCompleted { get; set; }
    public HotkeyConfig? Hotkeys { get; set; }

    /// <summary>Warn at 20 % and again at 5 %.</summary>
    public bool LowBatteryWarnings { get; set; } = true;

    /// <summary>Pause what is playing when the AirPods come out of your ears, start it again when one goes back in.</summary>
    public bool EarDetection { get; set; } = true;

    /// <summary>Which audio devices each mode switches to. Per user, because default devices are per user.</summary>
    public ModeAudio ConnectAudio { get; set; } = ModeAudio.Connect();
    public ModeAudio MusicAudio { get; set; } = ModeAudio.Music();
    public ModeAudio DisconnectAudio { get; set; } = ModeAudio.Disconnect();

    public ModeAudio For(ConnectMode mode) => mode == ConnectMode.Music ? MusicAudio : ConnectAudio;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(Paths.UserSettingsFile))
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Paths.UserSettingsFile), Options) ?? new UserSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file must not stop the app; the defaults are safe.
        }
        return new UserSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.UserDataDir);
        File.WriteAllText(Paths.UserSettingsFile, JsonSerializer.Serialize(this, Options));
    }

    public HotkeyConfig EffectiveHotkeys(PodGateConfig machine) => Hotkeys ?? machine.Hotkeys;
}
