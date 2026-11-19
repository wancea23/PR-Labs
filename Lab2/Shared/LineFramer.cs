using System.Text;

namespace CoffeeShop;

public sealed class LineFramer
{
    private readonly byte[] _buffer;
    private int _length;
    private bool _discarding;

    public LineFramer(int maxLineBytes) => _buffer = new byte[maxLineBytes];

    public int MaxLineBytes => _buffer.Length;

    public List<Frame> Feed(ReadOnlySpan<byte> data)
    {
        var frames = new List<Frame>();
        foreach (byte b in data)
        {
            if (b == (byte)'\n')
            {
                if (_discarding)
                    _discarding = false;
                else
                    frames.Add(Decode(_buffer.AsSpan(0, _length)));
                _length = 0;
            }
            else if (_discarding)
                continue;
            else if (_length == _buffer.Length)
            {
                frames.Add(Frame.Error($"message too long (at most {_buffer.Length} bytes)"));
                _discarding = true;
                _length = 0;
            }
            else
            {
                _buffer[_length++] = b;
            }
        }
        return frames;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static Frame Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            bytes = bytes[..^1];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Frame.Error("the message is not valid text");
        }
        foreach (char c in text)
            if (char.IsControl(c) && c != '\t')
                return Frame.Error("the message contains control characters");
        return Frame.Line(text);
    }
}

public readonly record struct Frame(string? Text, string? ErrorMessage)
{
    public static Frame Line(string text) => new(text, null);
    public static Frame Error(string message) => new(null, message);
    public bool IsError => ErrorMessage != null;
}

public static class Wire
{
    public static void SendLine(System.Net.Sockets.Socket socket, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        int sent = 0;
        while (sent < bytes.Length)
            sent += socket.Send(bytes, sent, bytes.Length - sent, System.Net.Sockets.SocketFlags.None);
    }
}
