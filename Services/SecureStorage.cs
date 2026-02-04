using System;
using System.Security.Cryptography;
using System.Text;

namespace SellerOps.App.Services
{
    public static class SecureStorage
    {
        public static string Protect(string plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText))
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

        public static string Unprotect(string encryptedText)
        {
            if (string.IsNullOrWhiteSpace(encryptedText))
                return encryptedText;

            try
            {
                var bytes = Convert.FromBase64String(encryptedText);
                var decrypted = ProtectedData.Unprotect(
                    bytes,
                    null,
                    DataProtectionScope.CurrentUser
                );

                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                // 1) Токен мог быть сохранён в старой версии без шифрования
                // 2) Или БД перенесли на другой ПК/пользователя, и DPAPI не может расшифровать
                // В любом случае не падаем — просто вернём исходную строку.
                return encryptedText;
            }
        }
    }
}
