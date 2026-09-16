using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PodGate.App;

public sealed class HotkeyEventArgs(int id) : EventArgs
{
    public int Id { get; } = id;
}

/// <summary>
/// Owns the global hotkeys. RegisterHotKey needs a window to deliver WM_HOTKEY to, so this is a hidden
/// one. Registration retries briefly: a combination another program has just released (for example a
/// shortcut's hotkey that Explorer still holds) can be unavailable for a moment.
/// </summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly List<int> _registered = [];

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        if (!RegisterHotKey(Handle, id, modifiers | ModNoRepeat, virtualKey)) return false;
        _registered.Add(id);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey) HotkeyPressed?.Invoke(this, new HotkeyEventArgs(m.WParam.ToInt32()));
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _registered) UnregisterHotKey(Handle, id);
        _registered.Clear();
        DestroyHandle();
    }
}

/// <summary>Turns "Ctrl+Alt+Shift+A" into what RegisterHotKey wants.</summary>
public static class Hotkey
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static (uint Modifiers, uint Key)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        uint modifiers = 0;
        string? keyName = null;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": modifiers |= ModWin; break;
                default: keyName = part; break;
            }
        }

        if (keyName is null || !Enum.TryParse(keyName, ignoreCase: true, out Keys key)) return null;
        return (modifiers, (uint)key);
    }
}
