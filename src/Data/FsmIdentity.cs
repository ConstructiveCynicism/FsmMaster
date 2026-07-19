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
    // RegexOptions.Compiled needs runtime code generation, which throws on this game's embedded Mono
    // (confirmed empirically - ArgumentOutOfRangeException out of the regex constructor itself). Plain
    // interpreted regex is fine here; this only runs once per FSM per scene scan, not per frame.
    private static readonly Regex DuplicateSuffixPattern = new(@"^(?<base>.+) \(\d+\)$");

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
