using UnityEngine;

namespace FsmMaster;

// IFsmHotkey wrapper over a plain KeyCode property - this loader generation has no
// KeyboardShortcut-equivalent modifier-combo type either, so this is a single key polled with
// Input.GetKeyDown, no modifier-combo support. Matches every other loader's own defaults, none of
// which actually used a modifier combo, so nothing is lost here.
internal sealed class KeyCodeHotkey : IFsmHotkey
{
    private readonly ConfigGetter<KeyCode> _getKeyCode;

    public KeyCodeHotkey(ConfigGetter<KeyCode> getKeyCode)
    {
        _getKeyCode = getKeyCode;
    }

    public bool IsDown()
    {
        KeyCode keyCode = _getKeyCode();
        return keyCode != KeyCode.None && Input.GetKeyDown(keyCode);
    }
}
