using UnityEngine;

namespace FsmMaster;

// IFsmPanelLayoutConfig wrapper over one position/size property pair on FsmMasterGlobalSettings - bound
// once per panel (FsmRightPanel, FsmMonitorPanel) against that panel's own RightPanel*/MonitorPanel*
// properties. See FsmMasterGlobalSettings.HasSavedLayout for the (-1, -1) "not yet saved" sentinel this
// reads.
internal sealed class FsmMasterPanelLayoutConfig : IFsmPanelLayoutConfig
{
    private readonly ConfigGetter<Vector2> _getPosition;
    private readonly ConfigGetter<Vector2> _getSize;

    public IFsmConfigValue<Vector2> Position { get; }
    public IFsmConfigValue<Vector2> Size { get; }

    public bool HasSavedPosition => FsmMasterGlobalSettings.HasSavedLayout(_getPosition());
    public bool HasSavedSize => FsmMasterGlobalSettings.HasSavedLayout(_getSize());

    public FsmMasterPanelLayoutConfig(ConfigGetter<Vector2> getPosition, ConfigSetter<Vector2> setPosition, ConfigGetter<Vector2> getSize, ConfigSetter<Vector2> setSize)
    {
        _getPosition = getPosition;
        _getSize = getSize;
        Position = new FieldConfigValue<Vector2>(getPosition, setPosition);
        Size = new FieldConfigValue<Vector2>(getSize, setSize);
    }
}
