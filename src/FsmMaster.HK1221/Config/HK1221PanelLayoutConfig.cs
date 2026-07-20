using System;
using UnityEngine;

namespace FsmMaster;

// IFsmPanelLayoutConfig wrapper over one position/size field pair on FsmMasterGlobalSettings - bound
// once per panel (FsmRightPanel, FsmMonitorPanel) against that panel's own RightPanel*/MonitorPanel*
// fields. See FsmMasterGlobalSettings.HasSavedLayout for the (-1, -1) "not yet saved" sentinel this
// reads.
internal sealed class HK1221PanelLayoutConfig : IFsmPanelLayoutConfig
{
    private readonly Func<Vector2> _getPosition;
    private readonly Func<Vector2> _getSize;

    public IFsmConfigValue<Vector2> Position { get; }
    public IFsmConfigValue<Vector2> Size { get; }

    public bool HasSavedPosition => FsmMasterGlobalSettings.HasSavedLayout(_getPosition());
    public bool HasSavedSize => FsmMasterGlobalSettings.HasSavedLayout(_getSize());

    public HK1221PanelLayoutConfig(Func<Vector2> getPosition, Action<Vector2> setPosition, Func<Vector2> getSize, Action<Vector2> setSize)
    {
        _getPosition = getPosition;
        _getSize = getSize;
        Position = new FieldConfigValue<Vector2>(getPosition, setPosition);
        Size = new FieldConfigValue<Vector2>(getSize, setSize);
    }
}
