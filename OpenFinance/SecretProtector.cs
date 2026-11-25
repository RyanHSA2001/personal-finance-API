using System.Security.Cryptography;
using System.Text;

namespace PersonalControl.Api.OpenFinance;

// Criptografia autenticada (AES-256-GCM) para segredos armazenados no banco de dados.
// Cada valor usa nonce aleatório de 96 bits, tag de 128 bits e vincula o userId como AAD
// (Associated Authenticated Data), impedindo reaproveitamento de ciphertexts entre usuários.
public class SecretProtector
{
    private const string Prefix = "v1:";
    private readonly byte[] _key;

    public SecretProtector(IConfiguration configuration)
    {
        // Usa Security:EncryptionKey se configurada; caso contrário deriva chave dedicada a partir de Jwt:Key.
        var rawSecret = configuration["Security:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(rawSecret))
            rawSecret = configuration["Jwt:Key"] ?? throw new InvalidOperationException("Chave de criptografia não configurada.");

        var ikm = Encoding.UTF8.GetBytes(rawSecret);
        var info = Encoding.UTF8.GetBytes("PersonalControl.OpenFinance.Credentials.AES256GCM.v1");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, outputLength: 32, salt: null, info: info);
    }

    public string Protect(Guid userId, string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];
        var aad = Encoding.UTF8.GetBytes(userId.ToString("N"));

        using var aes = new AesGcm(_key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag, aad);

        var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);

        return Prefix + Convert.ToBase64String(combined);
    }

    public string Unprotect(Guid userId, string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Formato de segredo criptografado inválido.");

        var combined = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (combined.Length < 28)
            throw new CryptographicException("Payload criptografado corrompido.");

        var nonce = combined.AsSpan(0, 12);
        var tag = combined.AsSpan(12, 16);
        var cipherBytes = combined.AsSpan(28);
        var plainBytes = new byte[cipherBytes.Length];
        var aad = Encoding.UTF8.GetBytes(userId.ToString("N"));

        using var aes = new AesGcm(_key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes, aad);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
