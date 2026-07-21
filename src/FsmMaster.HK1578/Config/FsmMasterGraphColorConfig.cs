using UnityEngine;

namespace FsmMaster;

// IFsmGraphColorConfig wrapper over FsmMasterGlobalSettings' Color properties.
internal sealed class FsmMasterGraphColorConfig : IFsmGraphColorConfig
{
    public IFsmConfigValue<Color>[] StateColors { get; }
    public IFsmConfigValue<Color>[] TransitionColors { get; }
    public IFsmConfigValue<Color> GlobalTransitionColor { get; }
    public IFsmConfigValue<Color> VignetteColor { get; }
    public IFsmConfigValue<Color> GlobalPseudoNodeColor { get; }
    public IFsmConfigValue<Color> GlobalPseudoNodeOutlineColor { get; }
    public IFsmConfigValue<Color> GlobalPseudoNodeTextColor { get; }
    public IFsmConfigValue<Color> NodeOutlineColor { get; }
    public IFsmConfigValue<Color> TransitionRowBackgroundColor { get; }
    public IFsmConfigValue<Color> ActiveStateColor { get; }
    public IFsmConfigValue<Color> ActiveTitleBackgroundColor { get; }
    public IFsmConfigValue<Color> ActiveTitleTextColor { get; }
    public IFsmConfigValue<Color> SelectedStateColor { get; }
    public IFsmConfigValue<Color> DisabledOutlineColor { get; }
    public IFsmConfigValue<Color> DisabledTitleTextColor { get; }
    public IFsmConfigValue<Color> DisabledEventTextColor { get; }
    public IFsmConfigValue<Color> DisabledTransitionLineColor { get; }
    public IFsmConfigValue<Color> DragTransitionColor { get; }

    public FsmMasterGraphColorConfig(FsmMasterGlobalSettings settings)
    {
        StateColors = new IFsmConfigValue<Color>[settings.StateColors.Length];
        for (int i = 0; i < settings.StateColors.Length; i++)
        {
            int index = i;
            StateColors[i] = new FieldConfigValue<Color>(() => settings.StateColors[index], v => settings.StateColors[index] = v);
        }

        TransitionColors = new IFsmConfigValue<Color>[settings.TransitionColors.Length];
        for (int i = 0; i < settings.TransitionColors.Length; i++)
        {
            int index = i;
            TransitionColors[i] = new FieldConfigValue<Color>(() => settings.TransitionColors[index], v => settings.TransitionColors[index] = v);
        }

        GlobalTransitionColor = new FieldConfigValue<Color>(() => settings.GlobalTransitionColor, v => settings.GlobalTransitionColor = v);
        VignetteColor = new FieldConfigValue<Color>(() => settings.VignetteColor, v => settings.VignetteColor = v);
        GlobalPseudoNodeColor = new FieldConfigValue<Color>(() => settings.GlobalPseudoNodeColor, v => settings.GlobalPseudoNodeColor = v);
        GlobalPseudoNodeOutlineColor = new FieldConfigValue<Color>(() => settings.GlobalPseudoNodeOutlineColor, v => settings.GlobalPseudoNodeOutlineColor = v);
        GlobalPseudoNodeTextColor = new FieldConfigValue<Color>(() => settings.GlobalPseudoNodeTextColor, v => settings.GlobalPseudoNodeTextColor = v);
        NodeOutlineColor = new FieldConfigValue<Color>(() => settings.NodeOutlineColor, v => settings.NodeOutlineColor = v);
        TransitionRowBackgroundColor = new FieldConfigValue<Color>(() => settings.TransitionRowBackgroundColor, v => settings.TransitionRowBackgroundColor = v);
        ActiveStateColor = new FieldConfigValue<Color>(() => settings.ActiveStateColor, v => settings.ActiveStateColor = v);
        ActiveTitleBackgroundColor = new FieldConfigValue<Color>(() => settings.ActiveTitleBackgroundColor, v => settings.ActiveTitleBackgroundColor = v);
        ActiveTitleTextColor = new FieldConfigValue<Color>(() => settings.ActiveTitleTextColor, v => settings.ActiveTitleTextColor = v);
        SelectedStateColor = new FieldConfigValue<Color>(() => settings.SelectedStateColor, v => settings.SelectedStateColor = v);
        DisabledOutlineColor = new FieldConfigValue<Color>(() => settings.DisabledOutlineColor, v => settings.DisabledOutlineColor = v);
        DisabledTitleTextColor = new FieldConfigValue<Color>(() => settings.DisabledTitleTextColor, v => settings.DisabledTitleTextColor = v);
        DisabledEventTextColor = new FieldConfigValue<Color>(() => settings.DisabledEventTextColor, v => settings.DisabledEventTextColor = v);
        DisabledTransitionLineColor = new FieldConfigValue<Color>(() => settings.DisabledTransitionLineColor, v => settings.DisabledTransitionLineColor = v);
        DragTransitionColor = new FieldConfigValue<Color>(() => settings.DragTransitionColor, v => settings.DragTransitionColor = v);
    }
}
