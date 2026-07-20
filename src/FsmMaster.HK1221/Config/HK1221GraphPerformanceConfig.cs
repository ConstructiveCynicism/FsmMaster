namespace FsmMaster;

// IFsmGraphPerformanceConfig wrapper over FsmMasterGlobalSettings' LineStyle/BoxStyle properties.
// Generation stays fixed at 0 - unlike the Silksong loader's ConfigurationManager, the old Modding
// API's GlobalSettings has no live in-game settings UI on this loader generation (see
// BRANCH_OVERVIEW.md), so these values can only change by hand-editing the settings JSON file and
// restarting, meaning FsmGraphOverlay's chrome-buffer cache never actually needs busting mid-session.
internal sealed class HK1221GraphPerformanceConfig : IFsmGraphPerformanceConfig
{
    public IFsmConfigValue<GraphLineStyle> LineStyle { get; }
    public IFsmConfigValue<GraphBoxStyle> BoxStyle { get; }
    public IFsmConfigValue<bool> DiagnosticsEnabled { get; }
    public int Generation => 0;

    public HK1221GraphPerformanceConfig(FsmMasterGlobalSettings settings)
    {
        LineStyle = new FieldConfigValue<GraphLineStyle>(() => settings.LineStyle, v => settings.LineStyle = v);
        BoxStyle = new FieldConfigValue<GraphBoxStyle>(() => settings.BoxStyle, v => settings.BoxStyle = v);
        DiagnosticsEnabled = new FieldConfigValue<bool>(() => settings.GraphDiagnostics, v => settings.GraphDiagnostics = v);
    }
}
