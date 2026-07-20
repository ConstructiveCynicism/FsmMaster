using BepInEx.Configuration;

namespace FsmMaster;

// Thin IFsmConfigValue<T> wrapper over a BepInEx ConfigEntry<T>, letting Core's config-consuming code
// (FsmGraphOverlay, FsmRightPanel, FsmMonitorPanel, ...) read/write a setting without depending on
// BepInEx.Configuration directly.
internal sealed class BepInExConfigValue<T> : IFsmConfigValue<T>
{
    private readonly ConfigEntry<T> _entry;

    public BepInExConfigValue(ConfigEntry<T> entry)
    {
        _entry = entry;
    }

    public T Value
    {
        get => _entry.Value;
        set => _entry.Value = value;
    }
}
