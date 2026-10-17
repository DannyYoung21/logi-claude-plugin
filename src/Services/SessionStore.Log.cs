namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.IO;

    // Session events also go to a file of our own: the Logi plugin log starts over on every plugin reload,
    // which loses exactly the history needed to explain a wrong status afterwards
    public partial class SessionStore
    {
        private static readonly String EventLogPath = Path.Combine(HookInstaller.DataDir, "session-events.log");
        private const Int64 EventLogMaxBytes = 512 * 1024;
        private static readonly Object EventLogLock = new();

        private static void Log(String message)
        {
            PluginLog.Info(message);
            try
            {
                lock (EventLogLock)
                {
                    var file = new FileInfo(EventLogPath);
                    if (file.Exists && file.Length > EventLogMaxBytes)
                    {
                        File.Move(EventLogPath, EventLogPath + ".1", overwrite: true);
                    }
                    File.AppendAllText(EventLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // Logging must never break session tracking
            }
        }
    }
}
