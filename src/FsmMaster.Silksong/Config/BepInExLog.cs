// SPDX-License-Identifier: EUPL-1.2
using BepInEx.Logging;

namespace FsmMaster;

// IFsmLog wrapper over a BepInEx ManualLogSource.
internal sealed class BepInExLog : IFsmLog
{
    private readonly ManualLogSource _logger;

    public BepInExLog(ManualLogSource logger)
    {
        _logger = logger;
    }

    public void LogInfo(string message) => _logger.LogInfo(message);

    public void LogWarning(string message) => _logger.LogWarning(message);

    public void LogError(string message) => _logger.LogError(message);
}
