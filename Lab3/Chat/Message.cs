using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LanChat;

public sealed class Message
{
    public const string Protocol = "lanchat/1";
    public const int MaxText = 500;
    public const int MaxDatagram = 4096;

    [JsonPropertyName("chat")] public string? Chat { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("seq")] public long? Seq { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex NamePattern = new("^[A-Za-z0-9_-]{1,20}$");
    private static readonly Regex IdPattern = new("^[0-9a-f]{16}$");

    private static readonly HashSet<string> Types = new()
    {
        "hello", "here", "taken", "join", "alive", "bye", "general", "private", "ack",
    };

    public static bool IsValidName(string name) => NamePattern.IsMatch(name);

    public byte[] ToBytes()
    {
        Chat = Protocol;
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Options));
    }

    public static Message? Parse(ReadOnlySpan<byte> bytes, out string error)
    {
        Message? m;
        try
        {
            m = JsonSerializer.Deserialize<Message>(bytes, Options);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or DecoderFallbackException)
        {
            error = "not a message of this chat (not JSON, or wrong field types)";
            return null;
        }
        error = m == null ? "empty message" : m.Check();
        return error == "" ? m : null;
    }

    private string Check()
    {
        if (Chat != Protocol) return "not a message of this chat (wrong protocol)";
        if (Type == null || !Types.Contains(Type)) return "unknown message type";
        if (Id == null || !IdPattern.IsMatch(Id)) return "missing or invalid id";
        if (Name == null || !NamePattern.IsMatch(Name)) return "missing or invalid name";
        switch (Type)
        {
            case "general":
                if (Text == null || Text.Length == 0) return "general message without text";
                if (Text.Length > MaxText) return $"text longer than {MaxText} characters";
                break;
            case "private":
                if (To == null || !NamePattern.IsMatch(To)) return "private message without a receiver";
                if (Seq == null || Nonce == null || Data == null) return "private message without seq, nonce or data";
                break;
            case "ack":
                if (Seq == null) return "ack without seq";
                break;
        }
        return "";
    }
}
