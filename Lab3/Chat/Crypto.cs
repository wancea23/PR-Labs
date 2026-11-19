using System.Security.Cryptography;
using System.Text;

namespace LanChat;

public sealed class Crypto
{
    public const int Iterations = 200_000;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public Crypto(string passphrase, int port)
    {
        byte[] salt = Encoding.UTF8.GetBytes($"{Message.Protocol}:{port}");
        _key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations,
            HashAlgorithmName.SHA256, 32);
    }

    public static byte[] AssociatedData(string fromId, string from, string to, long seq) =>
        Encoding.UTF8.GetBytes($"{fromId}|{from}|{to}|{seq}");

    public (string Nonce, string Data) Encrypt(string text, byte[] associatedData)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes(text);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag, associatedData);
        return (Convert.ToBase64String(nonce), Convert.ToBase64String(cipher.Concat(tag).ToArray()));
    }

    public string? Decrypt(string nonceText, string dataText, byte[] associatedData)
    {
        try
        {
            byte[] nonce = Convert.FromBase64String(nonceText);
            byte[] data = Convert.FromBase64String(dataText);
            if (nonce.Length != NonceSize || data.Length < TagSize) return null;
            byte[] cipher = data[..^TagSize];
            byte[] tag = data[^TagSize..];
            byte[] plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain, associatedData);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
