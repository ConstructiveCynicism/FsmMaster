using UnityEngine;

namespace FsmMaster;

// IFsmHotkey wrapper over a plain KeyCode field - the old Modding API's IModSettings has no
// KeyboardShortcut-equivalent modifier-combo type (see FsmMasterGlobalSettings), so this is a single
// key polled with Input.GetKeyDown, no modifier-combo support. Matches the Silksong loader's own
// KeyboardShortcut defaults, neither of which actually used a modifier combo, so nothing is lost here.
internal sealed class KeyCodeHotkey : IFsmHotkey
{
    private readonly System.Func<KeyCode> _getKeyCode;

    public KeyCodeHotkey(System.Func<KeyCode> getKeyCode)
    {
        _getKeyCode = getKeyCode;
    }

    public bool IsDown()
    {
        KeyCode keyCode = _getKeyCode();
        return keyCode != KeyCode.None && Input.GetKeyDown(keyCode);
    }
}
