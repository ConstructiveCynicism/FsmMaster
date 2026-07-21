using System;

namespace FsmMaster;

// IFsmLog wrapper over Modding.Loggable's Log/LogWarn/LogError instance methods, the same base every
// Mod on this loader generation inherits (confirmed against DebugMod.cs's own instance.Log/instance.LogWarn
// calls) - same shape as the hk1221 loader's own ModLog, just bound to Mod instead of Mod<,>.
internal sealed class ModLog : IFsmLog
{
    private readonly Action<string> _log;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logError;

    public ModLog(Action<string> log, Action<string> logWarn, Action<string> logError)
    {
        _log = log;
        _logWarn = logWarn;
        _logError = logError;
    }

    public void LogInfo(string message) => _log(message);

    public void LogWarning(string message) => _logWarn(message);

    public void LogError(string message) => _logError(message);
}
