// SPDX-License-Identifier: EUPL-1.2
using System;

namespace FsmMaster;

// Thin IFsmConfigValue<T> wrapper over a plain get/set delegate pair - the old Modding API's
// IModSettings has no ConfigEntry<T> equivalent (see FsmMasterSettings.cs: GlobalSettings is just a
// plain object the loader (de)serializes whole), so this reads/writes a settings object's own field or
// GetX/SetX-backed property directly instead of wrapping a loader-provided settings-entry type.
internal sealed class FieldConfigValue<T> : IFsmConfigValue<T>
{
    private readonly Func<T> _get;
    private readonly Action<T> _set;

    public FieldConfigValue(Func<T> get, Action<T> set)
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
