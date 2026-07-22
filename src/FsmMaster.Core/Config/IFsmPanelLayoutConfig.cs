// SPDX-License-Identifier: EUPL-1.2
using UnityEngine;

namespace FsmMaster;

// Persisted screen position/size for one draggable/resizable uGUI panel (FsmRightPanel,
// FsmMonitorPanel). (-1, -1) is each backing implementation's own "not yet saved" sentinel -
// HasSavedPosition/HasSavedSize report whether a real value has been saved yet.
public interface IFsmPanelLayoutConfig
{
    IFsmConfigValue<Vector2> Position { get; }

    IFsmConfigValue<Vector2> Size { get; }

    bool HasSavedPosition { get; }

    bool HasSavedSize { get; }
}
