// SPDX-License-Identifier: EUPL-1.2
using BepInEx.Configuration;

namespace FsmMaster;

// IFsmHotkey wrapper over a BepInEx ConfigEntry<KeyboardShortcut>.
internal sealed class BepInExHotkey : IFsmHotkey
{
    private readonly ConfigEntry<KeyboardShortcut> _entry;

    public BepInExHotkey(ConfigEntry<KeyboardShortcut> entry)
    {
        _entry = entry;
    }

    public bool IsDown() => _entry.Value.IsDown();
}
