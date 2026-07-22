// SPDX-License-Identifier: EUPL-1.2
namespace FsmMaster;

// IFsmGraphPerformanceConfig wrapper over FsmMasterGlobalSettings' LineStyle/BoxStyle properties.
// Generation stays fixed at 0 - this loader generation has no live in-game settings UI for these
// values either (ICustomMenuMod covers keybinds/toggles, not a general property grid), so they can
// only change by hand-editing the settings JSON and restarting, meaning FsmGraphOverlay's chrome-buffer
// cache never actually needs busting mid-session.
internal sealed class FsmMasterGraphPerformanceConfig : IFsmGraphPerformanceConfig
{
    public IFsmConfigValue<GraphLineStyle> LineStyle { get; }
    public IFsmConfigValue<GraphBoxStyle> BoxStyle { get; }
    public IFsmConfigValue<bool> DiagnosticsEnabled { get; }
    public int Generation => 0;

    public FsmMasterGraphPerformanceConfig(FsmMasterGlobalSettings settings)
    {
        LineStyle = new FieldConfigValue<GraphLineStyle>(() => settings.LineStyle, v => settings.LineStyle = v);
        BoxStyle = new FieldConfigValue<GraphBoxStyle>(() => settings.BoxStyle, v => settings.BoxStyle = v);
        DiagnosticsEnabled = new FieldConfigValue<bool>(() => settings.GraphDiagnostics, v => settings.GraphDiagnostics = v);
    }
}
