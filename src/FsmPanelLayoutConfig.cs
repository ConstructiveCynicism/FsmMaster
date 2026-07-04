using BepInEx.Configuration;
using UnityEngine;

namespace FsmMaster;

// Persists a uGUI panel's screen position/size across sessions - both FsmRightPanel and FsmMonitorPanel
// bind one of these so a dragged/resized layout survives a game restart. (-1, -1) is the "not yet saved"
// sentinel; the owning panel falls back to its own screen-relative default placement until the user
// actually drags/resizes it (see FsmRightPanel/FsmMonitorPanel's own Reposition). Hidden from
// Configuration Manager (Browsable = false) - this is auto-managed UI state, not a setting meant to be
// hand-edited there; dragging/resizing the panel itself is what changes it.
internal sealed class FsmPanelLayoutConfig
{
    private const string Section = "UI Layout";
    private static readonly Vector2 Unset = new(-1f, -1f);

    public ConfigEntry<Vector2> Position { get; }
    public ConfigEntry<Vector2> Size { get; }

    public bool HasSavedPosition => Position.Value.x >= 0f && Position.Value.y >= 0f;
    public bool HasSavedSize => Size.Value.x >= 0f && Size.Value.y >= 0f;

    private FsmPanelLayoutConfig(ConfigEntry<Vector2> position, ConfigEntry<Vector2> size)
    {
        Position = position;
        Size = size;
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
