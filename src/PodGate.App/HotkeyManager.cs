using System.Windows.Forms;
using PodGate.Core;

namespace PodGate.App;

public enum HotkeyAction
{
    Toggle = 1,
    Connect = 2,
    Release = 3,
    ConnectMusic = 4,
}

public enum BindResult
{
    Ok,
    Taken,
    Duplicate,
    Invalid,
}

/// <summary>
/// The four global shortcuts: which combination each action has, whether Windows let us register it, and
/// where a press goes. Setup and Settings change bindings through here, and test them by intercepting the
/// next press instead of running the action.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HotkeyWindow _window = new();
    private readonly Dictionary<HotkeyAction, string> _bindings = [];
    private readonly HashSet<HotkeyAction> _registered = [];

    public HotkeyManager(HotkeyConfig config)
    {
        _bindings[HotkeyAction.Toggle] = config.Toggle;
        _bindings[HotkeyAction.Connect] = config.Connect;
        _bindings[HotkeyAction.Release] = config.Release;
        _bindings[HotkeyAction.ConnectMusic] = config.ConnectMusic;

        _window.HotkeyPressed += (_, e) =>
        {
            var action = (HotkeyAction)e.Id;
            if (Interceptor?.Invoke(action) == true) return;
            Pressed?.Invoke(action);
        };
    }

    /// <summary>A shortcut was pressed and nobody is testing it.</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>Bindings were saved; menus showing shortcuts should refresh.</summary>
    public event Action? Changed;

    /// <summary>Returns true to swallow the press: used while a shortcut is being tested.</summary>
    public Func<HotkeyAction, bool>? Interceptor { get; set; }

    public string Get(HotkeyAction action) => _bindings[action];

    public bool IsRegistered(HotkeyAction action) => _registered.Contains(action);

    /// <summary>Registers every binding; returns the combinations Windows refused.</summary>
    public IReadOnlyList<string> RegisterAll()
    {
        var failed = new List<string>();
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            if (!Register(action) && !string.IsNullOrWhiteSpace(_bindings[action])) failed.Add(_bindings[action]);
        }
        return failed;
    }

    /// <summary>Stops listening while a new combination is being typed, so the old one cannot fire.</summary>
    public void Suspend()
    {
        foreach (HotkeyAction action in _registered.ToList()) Unregister(action);
    }

    public void Resume() => RegisterAll();

    public BindResult TryBind(HotkeyAction action, string combination)
    {
        if (Hotkey.Parse(combination) is not { } wanted) return BindResult.Invalid;
        foreach ((HotkeyAction other, string text) in _bindings)
        {
            if (other != action && Hotkey.Parse(text) == wanted) return BindResult.Duplicate;
        }

        string previous = _bindings[action];
        Unregister(action);
        _bindings[action] = combination;
        if (Register(action)) return BindResult.Ok;

        _bindings[action] = previous;
        Register(action);
        return BindResult.Taken;
    }

    public void Clear(HotkeyAction action)
    {
        Unregister(action);
        _bindings[action] = "";
    }

    /// <summary>Saves the current bindings as the user's own and tells the menu.</summary>
    public void Commit()
    {
        UserSettings settings = UserSettings.Load();
        settings.Hotkeys = new HotkeyConfig
        {
            Toggle = _bindings[HotkeyAction.Toggle],
            Connect = _bindings[HotkeyAction.Connect],
            Release = _bindings[HotkeyAction.Release],
            ConnectMusic = _bindings[HotkeyAction.ConnectMusic],
        };
        settings.Save();
        Changed?.Invoke();
    }

    private bool Register(HotkeyAction action)
    {
        if (_registered.Contains(action)) return true;
        if (Hotkey.Parse(_bindings[action]) is not { } parsed) return false;
        if (!_window.Register((int)action, parsed.Modifiers, parsed.Key)) return false;
        _registered.Add(action);
        return true;
    }

    private void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action)) _window.Unregister((int)action);
    }

    public void Dispose() => _window.Dispose();
}

/// <summary>How a combination is typed in and shown.</summary>
public static class HotkeyText
{
    /// <summary>"Ctrl+Alt+Shift+A" from a key press in a WPF window, or null for a bare modifier.</summary>
    public static string? FromKeyPress(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.None) return null;

        var parts = new List<string>();
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(((Keys)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)).ToString());
        return string.Join("+", parts);
    }

    /// <summary>Keycap labels: "D1" shows as "1", "OemPeriod" as ".".</summary>
    public static IReadOnlyList<string> Caps(string? combination)
    {
        if (string.IsNullOrWhiteSpace(combination)) return [];
        return combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part switch
            {
                { Length: 2 } when part[0] == 'D' && char.IsDigit(part[1]) => part[1..],
                "OemPeriod" => ".",
                "Oemcomma" => ",",
                "OemMinus" => "-",
                "Oemplus" => "=",
                "Space" => "Space",
                _ => part,
            })
            .ToList();
    }

    public static bool HasModifier(string combination) =>
        combination.Contains('+');
}
