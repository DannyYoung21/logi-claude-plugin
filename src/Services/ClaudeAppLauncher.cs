namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using Loupedeck.ClaudeControllerPlugin.Models;

    // Opens the Claude desktop app (claude:// deep links) or the terminal a session runs in
    public static class ClaudeAppLauncher
    {
        public const String ClaudeBundleId = "com.anthropic.claudefordesktop";

        public static void FocusClaude() => Run("open", "-b", ClaudeBundleId);

        // Desktop session: exactly this session in the Code tab. Terminal session: its terminal app to the front.
        public static void OpenSession(ClaudeSession session)
        {
            if (session == null)
            {
                FocusClaude();
            }
            else if (session.IsDesktop)
            {
                OpenUrl($"claude://code/continue?session={Uri.EscapeDataString(session.LocalId)}");
            }
            else
            {
                Run("open", "-a", TerminalApp(session.TermProgram));
            }
        }

        public static void OpenUrl(String url) => Run("open", url);

        // New code session, or the shared chat / Cowork composer (no "surface", so Claude picks what to start)
        public static void NewCodeSession() => OpenUrl("claude://code/new");

        public static void NewChat() => OpenUrl("claude://claude.ai/new");

        // Esc in the desktop app stops a running turn. Used when a stop cannot ride on a hook (pure text generation):
        // the session is opened first so Esc reaches the right one. Needs accessibility access for the Logi Plugin Service.
        public static void SendEscape(ClaudeSession session)
        {
            OpenSession(session);
            Thread.Sleep(700);
            Run("osascript",
                "-e", $"tell application id \"{ClaudeBundleId}\" to activate",
                "-e", "delay 0.2",
                "-e", "tell application \"System Events\" to key code 53");
        }

        // $TERM_PROGRAM of the session -> app to bring to the front
        private static String TerminalApp(String termProgram) => termProgram switch
        {
            "iTerm.app" => "iTerm",
            "WarpTerminal" => "Warp",
            "vscode" => "Visual Studio Code",
            "ghostty" => "Ghostty",
            "WezTerm" => "WezTerm",
            _ => "Terminal"
        };

        private static void Run(String fileName, params String[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }
                using var _ = Process.Start(psi);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not run {fileName} {String.Join(' ', args)}");
            }
        }
    }
}
