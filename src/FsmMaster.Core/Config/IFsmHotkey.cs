namespace FsmMaster;

// A single configurable keyboard shortcut, polled once per frame. The Silksong loader backs this
// with BepInEx's ConfigEntry<KeyboardShortcut>.Value.IsDown().
public interface IFsmHotkey
{
    bool IsDown();
}
