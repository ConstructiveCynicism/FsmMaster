using System;

namespace FsmMaster;

// IFsmLog wrapper over the old Modding API's Mod<,> logging methods. Log and LogWarn are confirmed
// against the real hk1221 build (DebugModSavestateCompat.cs's own working
// FsmMasterMod.Instance?.LogWarn(...)/.Log(...) calls) - LogError is assumed symmetric with LogWarn
// (both implementing Modding.ILogger, the same interface DebugMod's own logging goes through) but
// hasn't been independently exercised by any ported code path yet.
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
