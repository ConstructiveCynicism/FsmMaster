// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HutongGames.PlayMaker;

namespace FsmMaster;

// Groups live PlayMakerFSM components by a stable identity so edits made to one object apply to every
// scene-authored duplicate of it. Unity's editor "Duplicate" command appends " (1)", " (2)", etc. to the
// GameObject name (distinct from the "(Clone)" suffix runtime Instantiate calls append), so stripping that
// suffix recovers the shared base name duplicates were copied from.
internal static class FsmIdentity
{
    // RegexOptions.Compiled throws ArgumentOutOfRangeException on hk1221's embedded Mono runtime
    // (confirmed live - its old System.Text.RegularExpressions build validates options against a
    // narrower bitmask that doesn't include Compiled) - this pattern is matched rarely enough (once per
    // discovered PlayMakerFSM component name, not a hot path) that the JIT-compiled fast path isn't
    // worth losing net35 support over.
#if NET35
    private static readonly Regex DuplicateSuffixPattern = new(@"^(?<base>.+) \(\d+\)$");
#else
    private static readonly Regex DuplicateSuffixPattern = new(@"^(?<base>.+) \(\d+\)$", RegexOptions.Compiled);
#endif

    public static string GetObjectBaseName(string gameObjectName)
    {
        Match match = DuplicateSuffixPattern.Match(gameObjectName);
        return match.Success ? match.Groups["base"].Value : gameObjectName;
    }

    public static string GetFsmKey(string baseObjectName, string fsmName) => $"{baseObjectName}::{fsmName}";

    public static string GetFsmKey(PlayMakerFSM component) =>
        GetFsmKey(GetObjectBaseName(component.gameObject.name), component.FsmName);

    public static Dictionary<string, List<PlayMakerFSM>> DiscoverFsmGroups(IEnumerable<PlayMakerFSM> components)
    {
        var groups = new Dictionary<string, List<PlayMakerFSM>>();

        foreach (PlayMakerFSM component in components)
        {
            string key = GetFsmKey(component);
            if (!groups.TryGetValue(key, out List<PlayMakerFSM>? list))
            {
                list = new List<PlayMakerFSM>();
                groups[key] = list;
            }

            list.Add(component);
        }

        return groups;
    }
}
