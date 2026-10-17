namespace Loupedeck.ClaudeControllerPlugin.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;

    // Local HTTP endpoint the Claude Code hooks and the statusline bridge post to (127.0.0.1 only):
    //   POST /hook?event=<Event>        lifecycle and tool events
    //   POST /permission?event=…        permission requests (answered by the hook's stdout = our response body)
    //   POST /statusline                statusline JSON (rate limits, context window)
    // An empty response means "no decision": Claude Code then carries on as if the hook was not there.
    public class HookServer : IDisposable
    {
        private static readonly Lazy<HookServer> _instance = new(() => new HookServer());
        public static HookServer Instance => _instance.Value;

        private HttpListener _listener;

        public void Start()
        {
            try
            {
                this._listener = new HttpListener();
                this._listener.Prefixes.Add($"http://127.0.0.1:{HookInstaller.Port}/");
                this._listener.Start();
                _ = Task.Run(this.AcceptLoop);
                PluginLog.Info($"Hook server listening on 127.0.0.1:{HookInstaller.Port}");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Could not start the hook server on port {HookInstaller.Port}");
            }
        }

        private async Task AcceptLoop()
        {
            while (this._listener?.IsListening == true)
            {
                HttpListenerContext context;
                try
                {
                    context = await this._listener.GetContextAsync();
                }
                catch (Exception)
                {
                    break;  // listener stopped
                }
                _ = Task.Run(() => this.Handle(context));
            }
        }

        private async Task Handle(HttpListenerContext context)
        {
            String reply = null;
            try
            {
                var request = context.Request;
                String body;
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }

#if DEBUG
                // Development only: press a keypad key without the device, e.g. POST /debug/press?key=AllowOnce
                if (request.Url?.AbsolutePath == "/debug/press")
                {
                    reply = DebugPress(request.QueryString["key"], request.QueryString["title"]);
                }
                else
#endif
                if (request.HttpMethod == "POST" && !String.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    var payload = doc.RootElement;
                    var evt = request.QueryString["event"] ?? String.Empty;
                    var hints = new HookHints(request.QueryString["term"], request.QueryString["entry"],
                        Int32.TryParse(request.QueryString["pid"], out var pid) ? pid : 0);

                    switch (request.Url?.AbsolutePath)
                    {
                        case "/hook":
                        case "/permission":
                            reply = await SessionStore.Instance.HandleHookAsync(evt, payload, hints);
                            break;
                        case "/statusline":
                            if (payload.TryGetProperty("rate_limits", out var rateLimits))
                            {
                                UsageLimitService.Instance.ApplyStatuslineRateLimits(rateLimits);
                            }
                            SessionStore.Instance.ApplyStatusline(payload);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Hook request failed");
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(reply ?? String.Empty);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The hook gave up (timeout or Claude Code moved on); nothing to answer anymore
            }
        }

#if DEBUG
        // Acts only on the session with this exact title, so tests never touch real sessions
        private static String DebugPress(String key, String title)
        {
            if (key == "HooksOff" || key == "HooksOn")
            {
                if (key == "HooksOff") HookInstaller.Remove(); else HookInstaller.Install();
                return $"hooks installed: {HookInstaller.IsInstalled()}";
            }
            var store = SessionStore.Instance;
            var session = store.Sessions.FirstOrDefault(s => s.DisplayName == title);
            if (session == null)
            {
                return $"no session titled '{title}'";
            }
            switch (key)
            {
                case "Stop":
                    store.Stop(session);
                    break;
                default:
                    if (Enum.TryParse<Models.PermissionDecision>(key, out var decision))
                    {
                        store.AnswerPermission(session, decision);
                    }
                    break;
            }
            return $"{key} -> {session?.DisplayName ?? "no session"}";
        }
#endif

        public void Dispose()
        {
            try
            {
                this._listener?.Stop();
                this._listener?.Close();
            }
            catch (Exception)
            {
            }
            this._listener = null;
        }
    }

    // Extra facts the hook command adds to the URL: which terminal app runs the session, CLI or desktop app,
    // and the Claude Code process id
    public readonly record struct HookHints(String TermProgram, String Entrypoint, Int32 ProcessId);
}
