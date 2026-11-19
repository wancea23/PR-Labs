using System.Net.Sockets;
using System.Text;

namespace LanChat;

public static class Program
{
    private static readonly object ConsoleLock = new();
    private static readonly StringBuilder Input = new();
    private const string Prompt = "> ";
    private static bool _interactive;

    public static int Main(string[] args)
    {
        if (args.Length != 3 || !Message.IsValidName(args[0])
            || !int.TryParse(args[1], out int port) || port < 1 || port > 65535 || args[2].Length == 0)
        {
            Console.Error.WriteLine("usage: Chat <name> <port> <passphrase>");
            Console.Error.WriteLine("  name: 1 to 20 letters, digits, '_' or '-'; every peer of one chat uses the same port and passphrase");
            return 1;
        }
        _interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        Peer peer;
        try
        {
            peer = new Peer(args[0], port, args[2], Show);
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine($"cannot open the chat on port {port}: {e.Message}");
            return 1;
        }
        if (!peer.Join())
            return 2;

        Console.CancelKeyPress += (_, e) =>
        {
            peer.Close();
            lock (ConsoleLock) Console.WriteLine();
        };

        foreach (string line in ReadLines())
        {
            string text = line.Trim();
            if (text.Length == 0) continue;
            if (text == "/quit") break;
            if (text == "/users")
                Show(peer.UsersText());
            else if (text.StartsWith("/msg ") || text == "/msg")
            {
                string[] parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    Show("* usage: /msg name text");
                else if (TooLong(parts[2]))
                    continue;
                else
                    peer.SendPrivate(parts[1], parts[2]);
            }
            else if (text == "/help")
                Show("* text: to everybody | /msg name text: to name only | /users: who is online | /quit: leave");
            else if (text.StartsWith('/'))
                Show($"* unknown command {text.Split(' ')[0]} (try /help)");
            else if (!TooLong(text))
                peer.SendGeneral(text);
        }

        peer.Close();
        Show("* you left the chat");
        return 0;
    }

    private static bool TooLong(string text)
    {
        if (text.Length <= Message.MaxText) return false;
        Show($"* the message has {text.Length} characters; at most {Message.MaxText} are allowed");
        return true;
    }

    private static IEnumerable<string> ReadLines()
    {
        if (!_interactive)
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
                yield return line;
            yield break;
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
                    Console.Write("\r\u001b[2K");
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
            if (line != null)
                yield return line;
        }
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
