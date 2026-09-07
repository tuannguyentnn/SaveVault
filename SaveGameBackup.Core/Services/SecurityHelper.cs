using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Cung cấp thuật toán mã hóa đối xứng an toàn AES-256 kết hợp Salt ngẫu nhiên và PBKDF2
/// để bảo mật các thông tin nhạy cảm (OAuth Tokens, Client Secrets, Client IDs) khi lưu vào app_config.json.
/// </summary>
public static class SecurityHelper
{
    private const string EncryptedPrefix = "ENC:v1:";
    // Khóa nội bộ ứng dụng làm nền tảng kết hợp với Salt ngẫu nhiên của từng giá trị
    private static readonly byte[] MasterSecret = Encoding.UTF8.GetBytes("SaveVault_Secure_Credential_Engine_2026_@Key!");
    private const int Iterations = 100_000;
    private const int SaltSize = 16;   // 128 bit salt
    private const int KeySize = 32;    // 256 bit key
    private const int IvSize = 16;     // 128 bit IV

    /// <summary>
    /// Mã hóa một chuỗi bản rõ sử dụng AES-256 với Salt ngẫu nhiên và PBKDF2 (HMAC-SHA256).
    /// </summary>
    public static string EncryptWithSalt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        // 1. Sinh Salt ngẫu nhiên mật mã
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);

        // 2. Dẫn xuất khóa 256-bit và IV 128-bit thông qua PBKDF2 với Salt
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(MasterSecret, salt, Iterations, HashAlgorithmName.SHA256, KeySize + IvSize);
        byte[] key = derived[..KeySize];
        byte[] iv = derived[KeySize..];

        // 3. Mã hóa bằng AES-256-CBC
        byte[] cipherBytes;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                cs.Write(plainBytes, 0, plainBytes.Length);
                cs.FlushFinalBlock();
            }
            cipherBytes = ms.ToArray();
        }

        // 4. Đóng gói chuỗi an toàn: ENC:v1:<Base64(salt)>:<Base64(cipher)>
        return $"{EncryptedPrefix}{Convert.ToBase64String(salt)}:{Convert.ToBase64String(cipherBytes)}";
    }

    /// <summary>
    /// Giải mã chuỗi đã mã hóa bằng Salt và AES-256. Nếu chuỗi chưa mã hóa (legacy plain-text), trả về nguyên gốc.
    /// </summary>
    public static string DecryptWithSalt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        // Nếu không có prefix ENC:v1:, coi như là plain-text chưa mã hóa (tương thích ngược)
        if (!cipherText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return cipherText;
        }

        try
        {
            var raw = cipherText.Substring(EncryptedPrefix.Length);
            var parts = raw.Split(':');
            if (parts.Length != 2) return cipherText;

            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] cipherBytes = Convert.FromBase64String(parts[1]);

            // Dẫn xuất lại đúng key và IV từ Salt đã lưu
            byte[] derived = Rfc2898DeriveBytes.Pbkdf2(MasterSecret, salt, Iterations, HashAlgorithmName.SHA256, KeySize + IvSize);
            byte[] key = derived[..KeySize];
            byte[] iv = derived[KeySize..];

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var ms = new MemoryStream(cipherBytes);
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var reader = new StreamReader(cs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            // Nếu giải mã lỗi, trả về rỗng để an toàn
            return string.Empty;
        }
    }
}
