// SPDX-License-Identifier: EUPL-1.2
using UnityEngine;

namespace FsmMaster;

// Every named color FsmGraphOverlay draws the graph with, so the whole palette can be retuned by the
// loader's own settings UI without a recompile. See the Silksong loader's FsmGraphColorConfig for the
// concrete defaults each entry binds.
public interface IFsmGraphColorConfig
{
    IFsmConfigValue<Color>[] StateColors { get; }

    IFsmConfigValue<Color>[] TransitionColors { get; }

    IFsmConfigValue<Color> GlobalTransitionColor { get; }

    IFsmConfigValue<Color> VignetteColor { get; }

    IFsmConfigValue<Color> GlobalPseudoNodeColor { get; }

    IFsmConfigValue<Color> GlobalPseudoNodeOutlineColor { get; }

    IFsmConfigValue<Color> GlobalPseudoNodeTextColor { get; }

    IFsmConfigValue<Color> NodeOutlineColor { get; }

    IFsmConfigValue<Color> TransitionRowBackgroundColor { get; }

    IFsmConfigValue<Color> ActiveStateColor { get; }

    IFsmConfigValue<Color> ActiveTitleBackgroundColor { get; }

    IFsmConfigValue<Color> ActiveTitleTextColor { get; }

    IFsmConfigValue<Color> SelectedStateColor { get; }

    IFsmConfigValue<Color> DisabledOutlineColor { get; }

    IFsmConfigValue<Color> DisabledTitleTextColor { get; }

    IFsmConfigValue<Color> DisabledEventTextColor { get; }

    IFsmConfigValue<Color> DisabledTransitionLineColor { get; }

    IFsmConfigValue<Color> DragTransitionColor { get; }
}
