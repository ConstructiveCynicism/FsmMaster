// SPDX-License-Identifier: EUPL-1.2
namespace FsmMaster;

// Deliberately not System.Func<T>/System.Action<T>: hk1432's shipped mscorlib.dll is a managed-stripping
// build roughly 40% smaller than hk1578's, and closed generic instantiations of Func<T>/Action<T> over
// value types the base game itself never happened to use (bool, Vector2, Color, the graph style enums)
// are missing from it entirely - constructing one throws a TypeLoadException the instant the delegate
// type needs loading, regardless of how it's built (lambda, method group, or reflection all fail
// identically). A same-shape delegate pair declared in FsmMaster's own assembly is never touched by that
// stripping pass (it only runs over the game's own shipped assemblies at build time, before FsmMaster is
// even loaded), so this sidesteps the missing-instantiation problem entirely with no boxing or
// indirection - every call site here binds exactly as strongly-typed as it did with Func<T>/Action<T>.
internal delegate T ConfigGetter<T>();

internal delegate void ConfigSetter<T>(T value);

// Thin IFsmConfigValue<T> wrapper over a plain get/set delegate pair - this loader generation's
// IGlobalSettings<T> has no ConfigEntry<T> equivalent either (the settings object is JSON-serialized
// whole), so this reads/writes a settings object's own property directly instead of wrapping a
// loader-provided settings-entry type.
internal sealed class FieldConfigValue<T> : IFsmConfigValue<T>
{
    private readonly ConfigGetter<T> _get;
    private readonly ConfigSetter<T> _set;

    public FieldConfigValue(ConfigGetter<T> get, ConfigSetter<T> set)
    {
        _get = get;
        _set = set;
    }

    public T Value
    {
        get => _get();
        set => _set(value);
    }
}
