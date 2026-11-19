using System.Net.Sockets;

namespace CoffeeShop;

public sealed class Connection
{
    public Socket Socket { get; }
    public string Endpoint { get; }
    public bool IsProvider { get; set; }

    private readonly CancellationTokenSource _gone = new();
    public CancellationToken Gone => _gone.Token;

    private readonly object _sendLock = new();

    public Connection(Socket socket)
    {
        Socket = socket;
        Endpoint = socket.RemoteEndPoint?.ToString() ?? "?";
    }

    public bool Send(string line)
    {
        if (_gone.IsCancellationRequested) return false;
        try
        {
            lock (_sendLock)
                Wire.SendLine(Socket, line);
            return true;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Log.Write(Endpoint, $"error: could not send ({e.Message})");
            try { Socket.Shutdown(SocketShutdown.Both); } catch { }
            return false;
        }
    }

    public void MarkGone()
    {
        try { _gone.Cancel(); } catch (ObjectDisposedException) { }
    }
}
