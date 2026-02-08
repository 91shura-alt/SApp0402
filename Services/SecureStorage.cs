using System;
using System.Security.Cryptography;
using System.Text;

namespace SellerOps.App.Services
{
    public static class SecureStorage
    {
        private const string DpapiPrefix = "AQAAANCMnd8";

        public static string Protect(string plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText))
                return plainText;

            if (AppSettings.Instance.StoreTokensAsPlainText)
                return plainText;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(
                    bytes,
                    null,
                    DataProtectionScope.CurrentUser
                );

                return Convert.ToBase64String(encrypted);
            }
            catch
            {
                // В крайнем случае возвращаем как есть (чтобы приложение не падало).
                return plainText;
            }
        }

        public static bool TryUnprotect(string? encryptedText, out string? plainText)
        {
            plainText = null;

            if (string.IsNullOrWhiteSpace(encryptedText))
                return false;

            try
            {
                var bytes = Convert.FromBase64String(encryptedText);
                var decrypted = ProtectedData.Unprotect(
                    bytes,
                    null,
                    DataProtectionScope.CurrentUser
                );

                plainText = Encoding.UTF8.GetString(decrypted);
                return true;
            }
            catch (FormatException)
            {
                // Старая версия могла сохранить токен без шифрования.
                plainText = encryptedText;
                return true;
            }
            catch (CryptographicException)
            {
                if (encryptedText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                    return false;

                // На всякий случай — если строка не DPAPI, вернём как есть.
                plainText = encryptedText;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string Unprotect(string encryptedText)
        {
            if (string.IsNullOrWhiteSpace(encryptedText))
                return encryptedText;

            if (TryUnprotect(encryptedText, out var plainText) && !string.IsNullOrWhiteSpace(plainText))
                return plainText;

            // В крайнем случае возвращаем как есть (чтобы приложение не падало).
            return encryptedText;
        }
    }
}
