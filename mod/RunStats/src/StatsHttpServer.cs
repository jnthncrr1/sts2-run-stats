using System.Net;
using System.Reflection;
using System.Text;
using Godot;

namespace RunStats;

/// <summary>
/// Serves the viewer page and the live stats JSON on localhost so a browser can show the
/// table without any software outside the game. Only binds loopback, so nothing is exposed
/// to the network.
/// </summary>
public sealed class StatsHttpServer
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _page;
    private readonly int _port;

    public StatsHttpServer(int port)
    {
        _port = port;
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RunStats.index.html")
                         ?? throw new InvalidOperationException("index.html resource missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        _page = ms.ToArray();
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        try
        {
            _listener.Start();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[RunStats] could not start viewer server on port {_port}: {e.Message}");
            return;
        }
        _ = Task.Run(AcceptLoop);
        GD.Print($"[RunStats] viewer at http://localhost:{_port}/");
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            catch (Exception e)
            {
                GD.PushWarning("[RunStats] accept failed: " + e.Message);
                continue;
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/stats.json":
                    if (!File.Exists(StatsFile.CurrentPath))
                    {
                        Respond(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no stats yet"));
                        return;
                    }
                    Respond(ctx, 200, "application/json", File.ReadAllBytes(StatsFile.CurrentPath));
                    return;
                case "/":
                case "/index.html":
                    Respond(ctx, 200, "text/html; charset=utf-8", _page);
                    return;
                default:
                    Respond(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
                    return;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning("[RunStats] request failed: " + e.Message);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(body, 0, body.Length);
        ctx.Response.Close();
    }
}
