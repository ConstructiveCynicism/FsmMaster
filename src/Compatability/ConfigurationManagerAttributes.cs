namespace FsmMaster;

// Duck-typed metadata block BepInEx.ConfigurationManager reads via reflection off a ConfigDescription's
// Tags - it matches by property name alone, so this needs no reference to that mod's assembly. Only the
// one property FsmMaster actually needs (hiding auto-managed layout state from the settings list) is
// declared here; add more only if a future config entry needs them.
internal sealed class ConfigurationManagerAttributes
{
    public bool? Browsable;

    // Filters the setting out of the visible list entirely until the user checks Configuration
    // Manager's own global "Advanced settings" box - the closest thing that mod offers to a
    // collapsible section, since it has no per-category fold/collapse of its own.
    public bool? IsAdvanced;
}
