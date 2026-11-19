using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LanChat;

public sealed class PeerInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IPEndPoint EndPoint { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class Peer
{
    public static readonly IPAddress Group = IPAddress.Parse("239.255.42.99");
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AckWait = TimeSpan.FromSeconds(1);
    public const int MaxAttempts = 3;

    public string Name { get; }
    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public int Port { get; }

    private readonly Crypto _crypto;
    private readonly ChatLog _log;
    private readonly Action<string> _show;
    private readonly IPEndPoint _groupEndPoint;
    private readonly Socket _groupSocket;
    private readonly Socket _directSocket;

    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly object _peersLock = new();

    private readonly Dictionary<long, Pending> _pending = new();
    private readonly object _pendingLock = new();
    private long _lastSeq;

    private readonly HashSet<(string, long)> _seenPrivate = new();
    private readonly object _seenLock = new();

    private volatile bool _joined;
    private volatile bool _closing;
    private volatile string? _takenBy;

    private sealed class Pending
    {
        public required string To { get; init; }
        public required string Text { get; init; }
        public required byte[] Bytes { get; init; }
        public required IPEndPoint EndPoint { get; init; }
        public int Attempts { get; set; }
        public DateTime NextTry { get; set; }
    }

    public Peer(string name, int port, string passphrase, Action<string> show)
    {
        Name = name;
        Port = port;
        _show = show;
        _crypto = new Crypto(passphrase, port);
        _log = new ChatLog($"chat-{name}-{port}.log");
        _groupEndPoint = new IPEndPoint(Group, port);

        _groupSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _groupSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _groupSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        _groupSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(Group, IPAddress.Any));

        _directSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _directSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _directSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        _directSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
    }

    public int DirectPort => ((IPEndPoint)_directSocket.LocalEndPoint!).Port;

    public bool Join()
    {
        _log.Write("info", "-", "start", 0, $"name {Name}, id {Id}, chat port {Port}, own port {DirectPort}");
        Start(_groupSocket, "group");
        Start(_directSocket, "direct");
        SendGroup(new Message { Type = "hello", Id = Id, Name = Name });
        Thread.Sleep(JoinWait);
        if (_takenBy != null)
        {
            _show($"* the name {Name} is already used by the peer at {_takenBy}; start again with another name");
            Close(sayBye: false);
            return false;
        }
        _joined = true;
        SendGroup(new Message { Type = "join", Id = Id, Name = Name });
        new Thread(TimerLoop) { IsBackground = true, Name = "timer" }.Start();
        _show($"* you joined the chat as {Name} (port {Port}). {UsersText()}");
        return true;
    }

    private void Start(Socket socket, string which)
    {
        new Thread(() => ReceiveLoop(socket, which)) { IsBackground = true, Name = which }.Start();
    }

    public void SendGeneral(string text)
    {
        SendGroup(new Message { Type = "general", Id = Id, Name = Name, Text = text });
        _show($"[general] {Name}: {text}");
    }

    public void SendPrivate(string to, string text)
    {
        if (to == Name)
        {
            _show("* you cannot send a private message to yourself");
            return;
        }
        PeerInfo? peer;
        lock (_peersLock)
            peer = _peers.Values.FirstOrDefault(p => p.Name == to);
        if (peer == null)
        {
            _show($"* nobody named {to} is online (see /users)");
            return;
        }
        long seq = Interlocked.Increment(ref _lastSeq);
        var (nonce, data) = _crypto.Encrypt(text, Crypto.AssociatedData(Id, Name, to, seq));
        var m = new Message { Type = "private", Id = Id, Name = Name, To = to, Seq = seq, Nonce = nonce, Data = data };
        byte[] bytes = m.ToBytes();
        lock (_pendingLock)
            _pending[seq] = new Pending
            {
                To = to, Text = text, Bytes = bytes, EndPoint = peer.EndPoint, Attempts = 1,
                NextTry = DateTime.UtcNow + AckWait,
            };
        SendTo(bytes, peer.EndPoint, "private");
        _show($"[private to {to}] {Name}: {text}");
    }

    public string UsersText()
    {
        lock (_peersLock)
        {
            var others = _peers.Values.OrderBy(p => p.Name).ToList();
            var lines = new List<string> { $"Online: {others.Count + 1}" };
            lines.Add($"  {Name} (you) at port {DirectPort}");
            foreach (var p in others)
                lines.Add($"  {p.Name} at {p.EndPoint}, heard {(DateTime.UtcNow - p.LastSeen).TotalSeconds:0} s ago");
            return string.Join("\n", lines);
        }
    }

    public void Close(bool sayBye = true)
    {
        if (_closing) return;
        if (sayBye && _joined)
            SendGroup(new Message { Type = "bye", Id = Id, Name = Name });
        _closing = true;
        _groupSocket.Close();
        _directSocket.Close();
        _log.Write("info", "-", "stop", 0, "");
    }

    private void SendGroup(Message m) => SendTo(m.ToBytes(), _groupEndPoint, m.Type!);

    private void SendTo(byte[] bytes, IPEndPoint to, string kind)
    {
        try
        {
            _directSocket.SendTo(bytes, to);
            _log.Write("sent", to.ToString(), kind, bytes.Length, "");
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            _log.Write("error", to.ToString(), kind, bytes.Length, $"send failed: {e.Message}");
        }
    }

    private void ReceiveLoop(Socket socket, string which)
    {
        byte[] buffer = new byte[65536];
        while (!_closing)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                n = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                _log.Write("error", "-", which, 0, "the last datagram was not delivered (port unreachable)");
                continue;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }
            try
            {
                Handle(buffer.AsSpan(0, n), (IPEndPoint)from, which);
            }
            catch (Exception e)
            {
                _log.Write("dropped", from.ToString() ?? "?", "?", n, $"error while handling: {e.Message}");
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> bytes, IPEndPoint from, string which)
    {
        if (bytes.Length > Message.MaxDatagram)
        {
            _log.Write("dropped", from.ToString(), "?", bytes.Length, $"longer than {Message.MaxDatagram} bytes");
            return;
        }
        Message? m = Message.Parse(bytes, out string error);
        if (m == null)
        {
            _log.Write("dropped", from.ToString(), "?", bytes.Length, error);
            return;
        }
        if (m.Id == Id)
        {
            _log.Write("received", from.ToString(), m.Type!, bytes.Length, "own datagram, ignored");
            return;
        }
        _log.Write("received", from.ToString(), m.Type!, bytes.Length, which);

        switch (m.Type)
        {
            case "hello": OnHello(m, from); break;
            case "here": Remember(m, from, announce: false); break;
            case "taken": if (m.Name == Name && !_joined) _takenBy = from.ToString(); break;
            case "join": Remember(m, from, announce: true); break;
            case "alive": Remember(m, from, announce: true); break;
            case "bye": Forget(m.Id!, $"* {m.Name} left the chat"); break;
            case "general":
                if (!_joined) break;
                Touch(m, from);
                _show($"[general] {m.Name}: {m.Text}");
                break;
            case "private": OnPrivate(m, from, bytes.Length); break;
            case "ack":
                lock (_pendingLock)
                    _pending.Remove(m.Seq!.Value);
                break;
        }
    }

    private void OnHello(Message m, IPEndPoint from)
    {
        if (m.Name == Name)
        {
            bool win = _joined || string.CompareOrdinal(Id, m.Id) < 0;
            if (win)
                SendTo(new Message { Type = "taken", Id = Id, Name = Name }.ToBytes(), from, "taken");
            return;
        }
        if (_joined)
            SendTo(new Message { Type = "here", Id = Id, Name = Name }.ToBytes(), from, "here");
    }

    private void OnPrivate(Message m, IPEndPoint from, int size)
    {
        if (m.To != Name || !_joined)
        {
            _log.Write("dropped", from.ToString(), "private", size, $"addressed to {m.To}, not to me");
            return;
        }
        string? text = _crypto.Decrypt(m.Nonce!, m.Data!, Crypto.AssociatedData(m.Id!, m.Name!, m.To!, m.Seq!.Value));
        if (text == null)
        {
            _log.Write("dropped", from.ToString(), "private", size, "cannot decrypt: wrong key or changed on the way");
            return;
        }
        if (text.Length > Message.MaxText)
        {
            _log.Write("dropped", from.ToString(), "private", size, $"text longer than {Message.MaxText} characters");
            return;
        }
        SendTo(new Message { Type = "ack", Id = Id, Name = Name, Seq = m.Seq }.ToBytes(), from, "ack");
        bool first;
        lock (_seenLock)
            first = _seenPrivate.Add((m.Id!, m.Seq!.Value));
        if (!first) return;
        Touch(m, from);
        _show($"[private] {m.Name}: {text}");
    }

    private void Remember(Message m, IPEndPoint from, bool announce)
    {
        if (!_joined && m.Type != "here") return;
        bool isNew;
        lock (_peersLock)
        {
            isNew = !_peers.TryGetValue(m.Id!, out var p);
            if (isNew)
                _peers[m.Id!] = new PeerInfo { Id = m.Id!, Name = m.Name!, EndPoint = from, LastSeen = DateTime.UtcNow };
            else
            {
                p!.EndPoint = from;
                p.LastSeen = DateTime.UtcNow;
            }
        }
        if (isNew && announce && _joined)
            _show($"* {m.Name} joined the chat");
    }

    private void Touch(Message m, IPEndPoint from)
    {
        lock (_peersLock)
            if (_peers.TryGetValue(m.Id!, out var p))
            {
                p.LastSeen = DateTime.UtcNow;
                p.EndPoint = from;
            }
    }

    private void Forget(string id, string text)
    {
        bool removed;
        lock (_peersLock)
            removed = _peers.Remove(id);
        if (removed && _joined)
            _show(text);
    }

    private void TimerLoop()
    {
        DateTime nextBeat = DateTime.UtcNow + HeartbeatEvery;
        while (!_closing)
        {
            Thread.Sleep(200);
            DateTime now = DateTime.UtcNow;
            if (now >= nextBeat)
            {
                SendGroup(new Message { Type = "alive", Id = Id, Name = Name });
                nextBeat = now + HeartbeatEvery;
            }

            List<PeerInfo> gone;
            lock (_peersLock)
                gone = _peers.Values.Where(p => now - p.LastSeen > GoneAfter).ToList();
            foreach (var p in gone)
                Forget(p.Id, $"* {p.Name} left the chat (not heard from for {GoneAfter.TotalSeconds:0} s)");

            var resend = new List<Pending>();
            var failed = new List<Pending>();
            lock (_pendingLock)
                foreach (var (seq, p) in _pending.ToList())
                {
                    if (now < p.NextTry) continue;
                    if (p.Attempts >= MaxAttempts)
                    {
                        _pending.Remove(seq);
                        failed.Add(p);
                    }
                    else
                    {
                        p.Attempts++;
                        p.NextTry = now + AckWait;
                        resend.Add(p);
                    }
                }
            foreach (var p in resend)
                SendTo(p.Bytes, p.EndPoint, "private (again)");
            foreach (var p in failed)
                _show($"* {p.To} did not confirm your private message \"{Shorten(p.Text)}\" after {MaxAttempts} attempts");
        }
    }

    private static string Shorten(string s) => s.Length <= 30 ? s : s[..30] + "...";
}

public sealed class ChatLog
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public ChatLog(string path)
    {
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Write(string what, string endpoint, string kind, int size, string note)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what,-8} {endpoint,-21} {kind,-15} {size,5} bytes";
        if (note.Length > 0) line += "  " + note;
        lock (_lock)
            _writer.WriteLine(line);
    }
}
