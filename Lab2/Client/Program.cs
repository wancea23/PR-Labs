using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoffeeShop;

public static class Program
{
    private static readonly object ConsoleLock = new();
    private static readonly StringBuilder Input = new();
    private const string Prompt = "> ";
    private static bool _interactive;
    private static volatile bool _quitting;

    public static int Main(string[] args)
    {
        if (args.Length != 3 || (args[0] != "client" && args[0] != "provider")
            || !int.TryParse(args[2], out int port) || port < 1 || port > 65535)
        {
            Console.Error.WriteLine("usage: Client client|provider <server address> <port>");
            return 1;
        }
        bool provider = args[0] == "provider";

        Socket socket;
        try
        {
            socket = Connect(args[1], port);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            Console.Error.WriteLine($"cannot connect to {args[1]}:{port}: {e.Message}");
            return 1;
        }

        _interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        Show($"connected to {socket.RemoteEndPoint} as {args[0]} (type HELP, QUIT to leave)");

        var receiver = new Thread(() => ReceiveLoop(socket)) { IsBackground = true, Name = "receiver" };
        receiver.Start();

        try
        {
            if (provider)
                Wire.SendLine(socket, "PROVIDER");
            KeyboardLoop(socket);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }

        _quitting = true;
        receiver.Join(3000);
        try { socket.Close(); } catch { }
        return 0;
    }

    private static Socket Connect(string host, int port)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } : Dns.GetHostAddresses(host);
        SocketException? last = null;
        foreach (IPAddress address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Connect(new IPEndPoint(address, port));
                return socket;
            }
            catch (SocketException e)
            {
                last = e;
                socket.Dispose();
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static void ReceiveLoop(Socket socket)
    {
        var framer = new LineFramer(64 * 1024);
        var buffer = new byte[4096];
        string end = "the server closed the connection";
        try
        {
            while (true)
            {
                int n = socket.Receive(buffer);
                if (n == 0) break;
                foreach (Frame frame in framer.Feed(buffer.AsSpan(0, n)))
                    Show(frame.IsError ? $"(unreadable message from the server: {frame.ErrorMessage})" : frame.Text!);
            }
        }
        catch (SocketException e)
        {
            end = $"connection to the server lost ({e.SocketErrorCode})";
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (!_quitting)
        {
            lock (ConsoleLock)
            {
                if (_interactive) Console.Write("\r\u001b[2K");
                Console.WriteLine(end);
            }
            Environment.Exit(0);
        }
    }

    private static void KeyboardLoop(Socket socket)
    {
        if (!_interactive)
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
                if (SendCommand(socket, line)) return;
            return;
        }

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            string? line = null;
            lock (ConsoleLock)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    line = Input.ToString();
                    Input.Clear();
                    Console.WriteLine();
                    Console.Write(Prompt);
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (Input.Length > 0)
                    {
                        Input.Length--;
                        Console.Write("\b \b");
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    Input.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
            if (line != null && SendCommand(socket, line)) return;
        }
    }

    private static bool SendCommand(Socket socket, string line)
    {
        if (line.Trim().Length == 0) return false;
        Wire.SendLine(socket, line);
        return line.Trim().Equals("QUIT", StringComparison.OrdinalIgnoreCase);
    }

    private static void Show(string message)
    {
        lock (ConsoleLock)
        {
            if (!_interactive)
            {
                Console.WriteLine(message);
                return;
            }
            Console.Write("\r\u001b[2K");
            Console.WriteLine(message);
            Console.Write(Prompt + Input);
        }
    }
}
