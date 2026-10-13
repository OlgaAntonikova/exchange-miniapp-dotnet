using System.Security.Cryptography;
using System.Text;
using Exchange.Api.Settings;

namespace Exchange.Api.Data;

/// <summary>
/// Encrypts client PII at rest with AES-256-GCM (RULE-SECRETS, INV-13). A token is
/// <c>version (1 byte) | nonce (12 bytes) | ciphertext | tag (16 bytes)</c>; the version byte and the
/// column the token belongs to are bound in as associated data, so a token copied into another
/// column, or with its version altered, does not decrypt.
/// </summary>
public sealed class PiiCipher
{
    /// <summary>The token format this cipher writes and reads.</summary>
    public const byte TokenVersion = 1;

    /// <summary>AES-256 key length, in bytes: the one the settings check (spec 1.11).</summary>
    public const int KeyLengthBytes = SettingsLoader.KeyLengthBytes;

    /// <summary>The GCM standard nonce length, in bytes.</summary>
    public const int NonceLengthBytes = 12;

    /// <summary>The GCM tag length, in bytes.</summary>
    public const int TagLengthBytes = 16;

    /// <summary>The version byte's length, in bytes.</summary>
    public const int VersionLengthBytes = 1;

    private const int HeaderLengthBytes = VersionLengthBytes + NonceLengthBytes;

    private readonly byte[] _key;

    /// <summary>Creates a cipher from a 32-byte key; the cipher keeps its own copy.</summary>
    /// <exception cref="ArgumentException">The key is not 32 bytes long.</exception>
    public PiiCipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLengthBytes)
        {
            throw new ArgumentException($"PII key must be {KeyLengthBytes} bytes", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <summary>Creates a cipher from the base64 key of <c>PII_ENCRYPTION_KEY</c>.</summary>
    public static PiiCipher FromBase64(Secret key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bytes = Convert.FromBase64String(key.Reveal());
        try
        {
            return new PiiCipher(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Encrypts <paramref name="plaintext"/> for the column named by <paramref name="context"/>.</summary>
    public byte[] Encrypt(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(context); // no column, no binding (INV-13)
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var token = new byte[HeaderLengthBytes + plain.Length + TagLengthBytes];
        token[0] = TokenVersion;
        var nonce = token.AsSpan(VersionLengthBytes, NonceLengthBytes);
        RandomNumberGenerator.Fill(nonce);

        // One AesGcm per call: instances are cheap, and sharing one across threads is not documented
        // as safe.
        using var aes = new AesGcm(_key, TagLengthBytes);
        aes.Encrypt(nonce, plain, token.AsSpan(HeaderLengthBytes, plain.Length), token.AsSpan(HeaderLengthBytes + plain.Length), AssociatedData(TokenVersion, context));
        return token;
    }

    /// <summary>Decrypts a token written for <paramref name="context"/>.</summary>
    /// <exception cref="PiiDecryptionException">The token is malformed, altered, or for another key or column.</exception>
    public string Decrypt(byte[] token, string context)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrEmpty(context);
        if (token.Length < HeaderLengthBytes + TagLengthBytes)
        {
            throw new PiiDecryptionException("PII token is too short");
        }

        if (token[0] != TokenVersion)
        {
            throw new PiiDecryptionException("PII token has an unknown version");
        }

        var cipherLength = token.Length - HeaderLengthBytes - TagLengthBytes;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(_key, TagLengthBytes);
            aes.Decrypt(
                token.AsSpan(VersionLengthBytes, NonceLengthBytes),
                token.AsSpan(HeaderLengthBytes, cipherLength),
                token.AsSpan(HeaderLengthBytes + cipherLength),
                plain,
                AssociatedData(token[0], context));
        }
        catch (AuthenticationTagMismatchException error)
        {
            // Another key, another column, or an altered token: the reason cannot be told apart.
            throw new PiiDecryptionException("PII token failed authentication", error);
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] AssociatedData(byte version, string context) =>
        [version, .. Encoding.UTF8.GetBytes(context)];
}

/// <summary>A PII token could not be decrypted. The message never carries the token or any plaintext.</summary>
public sealed class PiiDecryptionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PiiDecryptionException()
        : base("PII token could not be decrypted")
    {
    }

    /// <summary>Creates the exception with a reason that holds no data.</summary>
    public PiiDecryptionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a reason and its cause.</summary>
    public PiiDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
