namespace FsmMaster;

// Loader-agnostic logging sink. The Silksong loader backs this with BepInEx's ManualLogSource; other
// loaders back it with whatever console/log API they provide.
public interface IFsmLog
{
    void LogInfo(string message);

    void LogWarning(string message);

    void LogError(string message);
}
