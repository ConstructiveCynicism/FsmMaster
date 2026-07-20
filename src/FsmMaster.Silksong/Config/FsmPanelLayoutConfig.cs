using BepInEx.Configuration;
using UnityEngine;

namespace FsmMaster;

// Persists a uGUI panel's screen position/size across sessions - both FsmRightPanel and FsmMonitorPanel
// bind one of these so a dragged/resized layout survives a game restart. (-1, -1) is the "not yet saved"
// sentinel; the owning panel falls back to its own screen-relative default placement until the user
// actually drags/resizes it (see FsmRightPanel/FsmMonitorPanel's own Reposition). Hidden from
// Configuration Manager (Browsable = false) - this is auto-managed UI state, not a setting meant to be
// hand-edited there; dragging/resizing the panel itself is what changes it.
internal sealed class FsmPanelLayoutConfig : IFsmPanelLayoutConfig
{
    private const string Section = "UI Layout";
    private static readonly Vector2 Unset = new(-1f, -1f);

    private readonly ConfigEntry<Vector2> _position;
    private readonly ConfigEntry<Vector2> _size;

    public IFsmConfigValue<Vector2> Position { get; }
    public IFsmConfigValue<Vector2> Size { get; }

    public bool HasSavedPosition => _position.Value.x >= 0f && _position.Value.y >= 0f;
    public bool HasSavedSize => _size.Value.x >= 0f && _size.Value.y >= 0f;

    private FsmPanelLayoutConfig(ConfigEntry<Vector2> position, ConfigEntry<Vector2> size)
    {
        _position = position;
        _size = size;
        Position = new BepInExConfigValue<Vector2>(position);
        Size = new BepInExConfigValue<Vector2>(size);
    }

    public static FsmPanelLayoutConfig Bind(ConfigFile config, string panelName)
    {
        var hidden = new ConfigurationManagerAttributes { Browsable = false };

        ConfigEntry<Vector2> position = config.Bind(
            Section,
            $"{panelName} Position",
            Unset,
            new ConfigDescription($"Saved screen position of the {panelName}, in pixels from the top-left. (-1, -1) means no saved position yet.", null, hidden));

        ConfigEntry<Vector2> size = config.Bind(
            Section,
            $"{panelName} Size",
            Unset,
            new ConfigDescription($"Saved size of the {panelName}, in pixels. (-1, -1) means no saved size yet.", null, hidden));

        return new FsmPanelLayoutConfig(position, size);
    }
}
