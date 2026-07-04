using System.Collections.Generic;
using UnityEngine;

namespace FsmMaster;

// Groups a flat FsmSnapshot into scene -> object -> FSM for drill-down selection (the old list
// panel, and now the Open dropdown), sorted alphabetically at each level so callers never have to
// format or sort anything themselves.
internal static class FsmDrilldownHierarchy
{
    public static List<SceneGroup> Build(FsmSnapshot snapshot)
    {
        var scenesByName = new Dictionary<string, SceneGroup>();
        var objectsByScene = new Dictionary<string, Dictionary<int, ObjectGroup>>();

        for (int i = 0; i < snapshot.Fsms.Count; i++)
        {
            FsmInfo fsm = snapshot.Fsms[i];

            // fsm.Component can already be destroyed (Unity fake-null) if this snapshot is being
            // rebuilt from a stale FsmSnapshot mid scene-transition, before RefreshSnapshot has
            // replaced it with a freshly-collected one - accessing .gameObject on it throws
            // NullReferenceException (see the identical guard in FsmGraphOverlay.ResolveFsmInfo).
            if (fsm.Component == null)
            {
                continue;
            }

            GameObject gameObject = fsm.Component.gameObject;
            string sceneName = gameObject.scene.name;

            if (!scenesByName.TryGetValue(sceneName, out SceneGroup? sceneGroup))
            {
                sceneGroup = new SceneGroup { SceneName = sceneName };
                scenesByName[sceneName] = sceneGroup;
                objectsByScene[sceneName] = new Dictionary<int, ObjectGroup>();
            }

            Dictionary<int, ObjectGroup> objectsById = objectsByScene[sceneName];
            int instanceId = gameObject.GetInstanceID();
            if (!objectsById.TryGetValue(instanceId, out ObjectGroup? objectGroup))
            {
                objectGroup = new ObjectGroup { InstanceId = instanceId, Label = fsm.GameObjectName };
                objectsById[instanceId] = objectGroup;
                sceneGroup.Objects.Add(objectGroup);
            }

            objectGroup.FsmIndices.Add(i);
            objectGroup.FsmLabels.Add(fsm.FsmName);
        }

        var sceneGroups = new List<SceneGroup>(scenesByName.Values);
        sceneGroups.Sort((a, b) => string.CompareOrdinal(a.SceneName, b.SceneName));

        foreach (SceneGroup sceneGroup in sceneGroups)
        {
            sceneGroup.Objects.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));

            foreach (ObjectGroup objectGroup in sceneGroup.Objects)
            {
                // FsmIndices/FsmLabels are parallel lists - sort a permutation of indices by label,
                // then rebuild both lists in that order so they stay aligned.
                var order = new List<int>(objectGroup.FsmLabels.Count);
                for (int i = 0; i < objectGroup.FsmLabels.Count; i++)
                {
                    order.Add(i);
                }

                order.Sort((a, b) => string.CompareOrdinal(objectGroup.FsmLabels[a], objectGroup.FsmLabels[b]));

                var sortedIndices = new List<int>(order.Count);
                var sortedLabels = new List<string>(order.Count);
                foreach (int i in order)
                {
                    sortedIndices.Add(objectGroup.FsmIndices[i]);
                    sortedLabels.Add(objectGroup.FsmLabels[i]);
                }

                objectGroup.FsmIndices = sortedIndices;
                objectGroup.FsmLabels = sortedLabels;
            }
        }

        return sceneGroups;
    }
}

// One entry per distinct loaded scene containing at least one live PlayMakerFSM instance - root
// level of the scene -> object -> FSM drill-down.
internal sealed class SceneGroup
{
    public string SceneName = "";
    public List<ObjectGroup> Objects = new();
}

// One entry per distinct GameObject (keyed by instance ID, not name - many enemy/object instances
// in this game share identical names) that has at least one live PlayMakerFSM.
internal sealed class ObjectGroup
{
    public int InstanceId;
    public string Label = "";
    public List<int> FsmIndices = new();
    public List<string> FsmLabels = new();
}
