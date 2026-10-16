using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RunStats;

/// <summary>
/// Minimal HTTP/1.1 server on both loopback addresses so any browser can show the table
/// without software outside the game. Raw sockets rather than HttpListener: the managed
/// HttpListener matches requests by Host header against the prefix list per address, so a
/// browser sending "Host: localhost" over 127.0.0.1 (Firefox) got a 404, and it rejects
/// bracketed IPv6 prefixes outright. Nothing here is reachable from other machines.
/// </summary>
public sealed class StatsHttpServer
{
    private readonly int _port;
    private readonly byte[] _page;
    private readonly Func<byte[]?> _readStats;
    private readonly Action<string> _log;
    private readonly List<TcpListener> _listeners = new();

    public StatsHttpServer(int port, byte[] page, Func<byte[]?> readStats, Action<string> log)
    {
        _port = port;
        _page = page;
        _readStats = readStats;
        _log = log;
    }

    public void Start()
    {
        foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var listener = new TcpListener(address, _port);
            try
            {
                listener.Start();
                _listeners.Add(listener);
                _ = Task.Run(() => AcceptLoop(listener));
            }
            catch (SocketException e)
            {
                _log($"[RunStats] could not listen on {address}:{_port}: {e.Message}");
            }
        }
        if (_listeners.Count > 0)
        {
            _log($"[RunStats] viewer at http://localhost:{_port}/");
        }
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception e)
            {
                _log("[RunStats] accept failed: " + e.Message);
                return;
            }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                NetworkStream stream = client.GetStream();
                string? requestLine = await ReadRequestAsync(stream);
                if (requestLine == null) return;
                string[] parts = requestLine.Split(' ');
                string path = parts.Length >= 2 ? parts[1] : "/";
                int query = path.IndexOf('?');
                if (query >= 0) path = path[..query];

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        await WriteResponse(stream, 200, "text/html; charset=utf-8", _page);
                        break;
                    case "/stats.json":
                    {
                        byte[]? stats = _readStats();
                        if (stats == null)
                            await WriteResponse(stream, 404, "text/plain", Encoding.UTF8.GetBytes("no stats yet"));
                        else
                            await WriteResponse(stream, 200, "application/json", stats);
                        break;
                    }
                    default:
                        await WriteResponse(stream, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
                        break;
                }
            }
            catch (Exception e)
            {
                _log("[RunStats] request failed: " + e.Message);
            }
        }
    }

    /// <summary>Reads through the end of the headers and returns the request line.</summary>
    private static async Task<string?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (n <= 0) break;
            total += n;
            string text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
            {
                int end = text.IndexOf('\n');
                return (end >= 0 ? text[..end] : text).TrimEnd('\r');
            }
        }
        return null;
    }

    private static async Task WriteResponse(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string reason = status == 200 ? "OK" : "Not Found";
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
