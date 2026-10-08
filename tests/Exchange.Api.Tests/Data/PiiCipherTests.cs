using System.Security.Cryptography;
using System.Text;
using Exchange.Api.Data;
using Exchange.Api.Settings;
using Shouldly;

namespace Exchange.Api.Tests.Data;

/// <summary>
/// INV-13 for the cipher itself (RULE-TEST-FIRST): what goes in comes out only with the same key and
/// the same column, every alteration is refused loudly, and nothing readable is left in the token.
/// </summary>
public sealed class PiiCipherTests
{
    private const string IbanColumn = "orders.iban";
    private const string NameColumn = "orders.recipient_name";
    private const string Iban = "ME25505000012345678951";

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes);

    [Theory]
    [InlineData(Iban)]
    [InlineData("")]
    [InlineData("Ivan Petrov")]
    [InlineData("\u00c5ngstr\u00f6m, \u00e9t\u00e9, \u6771\u4eac")] // accented Latin and CJK: multibyte UTF-8
    public void A_value_round_trips_with_the_same_key_and_column(string plaintext)
    {
        var cipher = new PiiCipher(Key);

        cipher.Decrypt(cipher.Encrypt(plaintext, IbanColumn), IbanColumn).ShouldBe(plaintext);
    }

    [Fact]
    public void A_token_has_the_documented_layout_and_no_readable_plaintext()
    {
        var token = new PiiCipher(Key).Encrypt(Iban, IbanColumn);

        token.Length.ShouldBe(PiiCipher.VersionLengthBytes + PiiCipher.NonceLengthBytes + Encoding.UTF8.GetByteCount(Iban) + PiiCipher.TagLengthBytes);
        token[0].ShouldBe(PiiCipher.TokenVersion);
        Encoding.UTF8.GetString(token).ShouldNotContain("5050000123");
        Convert.ToHexString(token).ShouldNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("ME25")));
    }

    [Fact]
    public void A_token_is_the_documented_format_decryptable_without_the_cipher()
    {
        // A known-answer check independent of PiiCipher: tokens are stored, so the layout and the
        // associated data must stay exactly as documented for rows written today.
        var token = new PiiCipher(Key).Encrypt(Iban, IbanColumn);
        var nonce = token[PiiCipher.VersionLengthBytes..(PiiCipher.VersionLengthBytes + PiiCipher.NonceLengthBytes)];
        var ciphertext = token[(PiiCipher.VersionLengthBytes + PiiCipher.NonceLengthBytes)..^PiiCipher.TagLengthBytes];
        var tag = token[^PiiCipher.TagLengthBytes..];
        byte[] associatedData = [PiiCipher.TokenVersion, .. Encoding.UTF8.GetBytes(IbanColumn)];
        var plain = new byte[ciphertext.Length];

        using (var aes = new AesGcm(Key, PiiCipher.TagLengthBytes))
        {
            aes.Decrypt(nonce, ciphertext, tag, plain, associatedData);
        }

        Encoding.UTF8.GetString(plain).ShouldBe(Iban);
    }

    [Fact]
    public void A_token_written_outside_the_cipher_in_the_documented_format_decrypts()
    {
        var nonce = RandomNumberGenerator.GetBytes(PiiCipher.NonceLengthBytes);
        var plain = Encoding.UTF8.GetBytes(Iban);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[PiiCipher.TagLengthBytes];
        byte[] associatedData = [PiiCipher.TokenVersion, .. Encoding.UTF8.GetBytes(IbanColumn)];
        using (var aes = new AesGcm(Key, PiiCipher.TagLengthBytes))
        {
            aes.Encrypt(nonce, plain, ciphertext, tag, associatedData);
        }

        byte[] token = [PiiCipher.TokenVersion, .. nonce, .. ciphertext, .. tag];

        new PiiCipher(Key).Decrypt(token, IbanColumn).ShouldBe(Iban);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_missing_column_name_is_refused(string? context)
    {
        var cipher = new PiiCipher(Key);

        Should.Throw<ArgumentException>(() => cipher.Encrypt(Iban, context!));
        Should.Throw<ArgumentException>(() => cipher.Decrypt(new byte[64], context!));
    }

    [Fact]
    public void The_same_value_encrypts_differently_each_time()
    {
        var cipher = new PiiCipher(Key);

        var first = cipher.Encrypt(Iban, IbanColumn);
        var second = cipher.Encrypt(Iban, IbanColumn);

        first.ShouldNotBe(second);
        first.AsSpan(PiiCipher.VersionLengthBytes, PiiCipher.NonceLengthBytes).ToArray()
            .ShouldNotBe(second.AsSpan(PiiCipher.VersionLengthBytes, PiiCipher.NonceLengthBytes).ToArray());
    }

    [Fact]
    public void Another_key_cannot_decrypt()
    {
        var token = new PiiCipher(Key).Encrypt(Iban, IbanColumn);
        var other = new PiiCipher(RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes));

        Should.Throw<PiiDecryptionException>(() => other.Decrypt(token, IbanColumn));
    }

    [Fact]
    public void A_token_moved_to_another_column_cannot_be_decrypted()
    {
        var cipher = new PiiCipher(Key);
        var token = cipher.Encrypt(Iban, IbanColumn);

        Should.Throw<PiiDecryptionException>(() => cipher.Decrypt(token, NameColumn));
    }

    [Theory]
    [InlineData(0)] // the version byte
    [InlineData(1)] // the nonce
    [InlineData(PiiCipher.VersionLengthBytes + PiiCipher.NonceLengthBytes)] // the ciphertext
    [InlineData(-1)] // the tag (last byte)
    public void Any_altered_byte_is_refused(int position)
    {
        var cipher = new PiiCipher(Key);
        var token = cipher.Encrypt(Iban, IbanColumn);
        var index = position < 0 ? token.Length + position : position;
        token[index] ^= 0x01;

        Should.Throw<PiiDecryptionException>(() => cipher.Decrypt(token, IbanColumn));
    }

    [Fact]
    public void An_unknown_version_is_refused_even_with_a_valid_rest()
    {
        var cipher = new PiiCipher(Key);
        var token = cipher.Encrypt(Iban, IbanColumn);
        token[0] = PiiCipher.TokenVersion + 1;

        Should.Throw<PiiDecryptionException>(() => cipher.Decrypt(token, IbanColumn)).Message.ShouldContain("version");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PiiCipher.VersionLengthBytes + PiiCipher.NonceLengthBytes + PiiCipher.TagLengthBytes - 1)]
    public void A_token_too_short_to_hold_a_tag_is_refused(int length)
    {
        var token = new byte[length];
        if (length > 0)
        {
            token[0] = PiiCipher.TokenVersion;
        }

        Should.Throw<PiiDecryptionException>(() => new PiiCipher(Key).Decrypt(token, IbanColumn));
    }

    [Fact]
    public void A_refusal_carries_no_data()
    {
        var token = new PiiCipher(Key).Encrypt(Iban, IbanColumn);
        var other = new PiiCipher(RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes));

        var error = Should.Throw<PiiDecryptionException>(() => other.Decrypt(token, IbanColumn));

        error.ToString().ShouldNotContain(Iban);
        error.ToString().ShouldNotContain(Convert.ToBase64String(token));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_key_of_the_wrong_length_is_refused(int length) =>
        Should.Throw<ArgumentException>(() => new PiiCipher(new byte[length]));

    [Fact]
    public void The_settings_key_is_read_as_base64()
    {
        var cipher = PiiCipher.FromBase64(new Secret(Convert.ToBase64String(Key)));

        new PiiCipher(Key).Decrypt(cipher.Encrypt(Iban, IbanColumn), IbanColumn).ShouldBe(Iban);
    }

    [Fact]
    public void The_cipher_keeps_its_own_copy_of_the_key()
    {
        var key = RandomNumberGenerator.GetBytes(PiiCipher.KeyLengthBytes);
        var cipher = new PiiCipher(key);
        var token = cipher.Encrypt(Iban, IbanColumn);

        Array.Clear(key);

        cipher.Decrypt(token, IbanColumn).ShouldBe(Iban);
    }
}
