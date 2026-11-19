using System.Net;
using System.Net.Sockets;

namespace CoffeeShop;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out int port) || port < 1 || port > 65535)
        {
            Console.Error.WriteLine("usage: Server <port> [--stock <file>]");
            Console.Error.WriteLine("  --stock <file>  keep the stock in this file, so it survives a restart");
            return 1;
        }
        string? stockFile = null;
        if (args.Length >= 3 && args[1] == "--stock")
            stockFile = args[2];

        var server = new Server(port, stockFile);
        server.Run();
        return 0;
    }
}

public static class Log
{
    private static readonly object ConsoleLock = new();

    public static void Write(string endpoint, string message)
    {
        lock (ConsoleLock)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{endpoint}] {message}");
    }
}

public sealed class Server
{
    private const int MaxLineBytes = 1024;
    private const int MaxQuantity = 1000;

    private readonly int _port;
    private readonly Shop _shop;
    private readonly OrderQueue _queue = new();
    private int _lastOrderId;

    private Connection? _provider;
    private readonly object _providerLock = new();

    public Server(int port, string? stockFile)
    {
        _port = port;
        _shop = new Shop(stockFile);
    }

    public void Run()
    {
        int workers = Environment.ProcessorCount;
        for (int i = 1; i <= workers; i++)
        {
            int id = i;
            new Thread(() => WorkerLoop(id)) { IsBackground = true, Name = $"worker {id}" }.Start();
        }

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Any, _port));
        listener.Listen(100);
        Log.Write("server", $"listening on port {_port}, {workers} workers, stock: {_shop.StockText()}");

        while (true)
        {
            Socket socket;
            try
            {
                socket = listener.Accept();
            }
            catch (SocketException e)
            {
                Log.Write("server", $"error: accept failed ({e.Message})");
                continue;
            }
            var conn = new Connection(socket);
            new Thread(() => ClientLoop(conn)) { IsBackground = true, Name = $"client {conn.Endpoint}" }.Start();
        }
    }

    private void ClientLoop(Connection conn)
    {
        Socket s = conn.Socket;
        Log.Write(conn.Endpoint, "connected");
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException)
        {
            Log.Write(conn.Endpoint, $"keepalive not available ({e.Message})");
        }
        s.SendTimeout = 5000;

        conn.Send("WELCOME to the coffee shop. Commands: MENU, ORDER <qty> <product> ..., PROVIDER, DELIVER <qty> <product> ..., HELP, QUIT");

        var framer = new LineFramer(MaxLineBytes);
        var buffer = new byte[4096];
        string reason = "closed the connection";
        try
        {
            while (true)
            {
                int n = s.Receive(buffer);
                if (n == 0) break;
                foreach (Frame frame in framer.Feed(buffer.AsSpan(0, n)))
                {
                    if (frame.IsError)
                        Error(conn, frame.ErrorMessage!);
                    else if (!Handle(conn, frame.Text!))
                    {
                        reason = "quit";
                        return;
                    }
                }
            }
        }
        catch (SocketException e)
        {
            reason = $"disappeared ({e.SocketErrorCode})";
        }
        catch (ObjectDisposedException)
        {
            reason = "disappeared";
        }
        finally
        {
            Disconnect(conn, reason);
        }
    }

    private bool Handle(Connection conn, string line)
    {
        string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            Error(conn, "empty message");
            return true;
        }
        string command = words[0].ToUpperInvariant();
        string[] rest = words[1..];

        switch (command)
        {
            case "MENU":
                if (rest.Length != 0) { Error(conn, "MENU takes no parameters"); break; }
                conn.Send(_shop.Menu());
                break;

            case "ORDER":
                Order(conn, rest);
                break;

            case "PROVIDER":
                BecomeProvider(conn);
                break;

            case "DELIVER":
                Deliver(conn, rest);
                break;

            case "HELP":
                conn.Send("HELP MENU | ORDER 2 croissant 1 tea 1 coffee | PROVIDER | DELIVER 20 coffee 10 croissant (provider only) | QUIT");
                break;

            case "QUIT":
                conn.Send("BYE");
                return false;

            default:
                Error(conn, $"unknown command '{Shorten(words[0])}' (try HELP)");
                break;
        }
        return true;
    }

    private Dictionary<string, int>? ParseItems(Connection conn, string command, string[] p)
    {
        if (p.Length == 0)
        {
            Error(conn, $"{command} needs at least one item, for example: {command} 2 croissant 1 tea");
            return null;
        }
        if (p.Length % 2 != 0)
        {
            Error(conn, $"{command} needs pairs of <quantity> <product>, for example: {command} 2 croissant 1 tea");
            return null;
        }
        var items = new Dictionary<string, int>();
        for (int i = 0; i < p.Length; i += 2)
        {
            if (!int.TryParse(p[i], out int qty) || qty <= 0)
            {
                Error(conn, $"'{Shorten(p[i])}' is not a positive quantity");
                return null;
            }
            string product = p[i + 1].ToLowerInvariant();
            if (!Shop.IsProduct(product))
            {
                Error(conn, $"unknown product '{Shorten(p[i + 1])}' (we have coffee, tea, croissant)");
                return null;
            }
            items.TryGetValue(product, out int already);
            if (qty > MaxQuantity || already + qty > MaxQuantity)
            {
                Error(conn, $"at most {MaxQuantity} {product} at once");
                return null;
            }
            items[product] = already + qty;
        }
        return items;
    }

    private void Order(Connection conn, string[] p)
    {
        var items = ParseItems(conn, "ORDER", p);
        if (items == null) return;

        TakeResult taken = _shop.TryTake(items);
        string text = string.Join(", ", items.Select(i => $"{i.Value} {i.Key}"));
        if (!taken.Ok)
        {
            Log.Write(conn.Endpoint, $"order refused: {text} (only {taken.Left} {taken.Missing} left)");
            conn.Send($"REFUSED not enough {taken.Missing}: only {taken.Left} left");
            return;
        }

        int seconds = items.Sum(i => i.Value * Shop.SecondsOf(i.Key));
        var order = new Order
        {
            Id = Interlocked.Increment(ref _lastOrderId),
            Owner = conn,
            Items = items,
            Seconds = seconds,
        };
        Log.Write(conn.Endpoint, $"order {order.Id} accepted: {text}, {seconds} s, {_queue.Count} orders before it in the queue");
        conn.Send($"ACCEPTED order {order.Id}: {text}, ready in {seconds} s");
        _queue.Add(order);

        foreach (var (name, left) in taken.NowLow)
            Alert(name, left);
    }

    private void BecomeProvider(Connection conn)
    {
        lock (_providerLock)
        {
            if (conn.IsProvider)
            {
                Error(conn, "you are already the provider");
                return;
            }
            if (_provider != null)
            {
                Error(conn, $"a provider is already connected ({_provider.Endpoint})");
                return;
            }
            _provider = conn;
            conn.IsProvider = true;
        }
        Log.Write(conn.Endpoint, "is the provider");
        conn.Send($"OK you are the provider. Stock: {_shop.StockText()}");

        foreach (var (name, left) in _shop.LowProducts())
            Alert(name, left);
    }

    private void Deliver(Connection conn, string[] p)
    {
        if (!conn.IsProvider)
        {
            Log.Write(conn.Endpoint, "error: a client tried to deliver");
            conn.Send("ERROR only the provider can deliver (send PROVIDER first)");
            return;
        }
        var items = ParseItems(conn, "DELIVER", p);
        if (items == null) return;

        string stock = _shop.Add(items);
        string text = string.Join(", ", items.Select(i => $"{i.Value} {i.Key}"));
        Log.Write(conn.Endpoint, $"delivery: {text}; stock now {stock}");
        conn.Send($"DELIVERED {text}. Stock now: {stock}");
    }

    private void Alert(string product, int left)
    {
        Connection? provider;
        lock (_providerLock)
            provider = _provider;
        if (provider == null)
        {
            Log.Write("server", $"{product} is low ({left} left), no provider connected: it gets the alert when it connects");
            return;
        }
        Log.Write(provider.Endpoint, $"alert sent: {product} only {left} left");
        provider.Send($"ALERT {product}: only {left} left");
    }

    private void Error(Connection conn, string message)
    {
        Log.Write(conn.Endpoint, $"error: {message}");
        conn.Send($"ERROR {message}");
    }

    private static string Shorten(string s) => s.Length <= 20 ? s : s[..20] + "...";

    private void Disconnect(Connection conn, string reason)
    {
        conn.MarkGone();

        List<Order> waiting = _queue.RemoveAllOf(conn);
        foreach (Order o in waiting)
        {
            string stock = _shop.Add(o.Items);
            Log.Write(conn.Endpoint, $"order {o.Id} cancelled while waiting, {o.ItemsText} back in stock ({stock})");
        }

        lock (_providerLock)
            if (_provider == conn)
                _provider = null;

        try { conn.Socket.Shutdown(SocketShutdown.Both); } catch { }
        conn.Socket.Close();
        Log.Write(conn.Endpoint, $"{(conn.IsProvider ? "provider" : "client")} {reason}");
    }

    private void WorkerLoop(int id)
    {
        while (true)
        {
            Order order = _queue.Take();
            Connection owner = order.Owner;
            Log.Write(owner.Endpoint, $"worker {id} prepares order {order.Id} ({order.ItemsText}, {order.Seconds} s)");

            bool cancelled = owner.Gone.WaitHandle.WaitOne(TimeSpan.FromSeconds(order.Seconds));
            if (cancelled)
            {
                string stock = _shop.Add(order.Items);
                Log.Write(owner.Endpoint, $"worker {id} stopped order {order.Id}: the client is gone, {order.ItemsText} back in stock ({stock})");
                continue;
            }
            Log.Write(owner.Endpoint, $"order {order.Id} ready (worker {id})");
            owner.Send($"READY order {order.Id}: {order.ItemsText}");
        }
    }
}
