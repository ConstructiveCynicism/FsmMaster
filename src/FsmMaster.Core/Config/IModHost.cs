namespace FsmMaster;

// What a loader provides beyond config values: logging, where this mod may persist its own files, and
// its own reported version. No Core code consumes StoragePath/Version yet - FsmSaveDataStore still
// roots itself directly at Application.persistentDataPath, a plain UnityEngine API available on every
// target - but the seam is defined now so a loader whose storage location or versioning differs (the
// old Modding API has no persistentDataPath equivalent) has somewhere to plug in later without another
// interface change.
public interface IModHost
{
    IFsmLog Log { get; }

    string StoragePath { get; }

    string Version { get; }
}
