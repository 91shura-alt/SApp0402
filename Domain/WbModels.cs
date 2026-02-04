namespace SellerOps.App.Domain
{
    public class ApiToken
    {
        public int Id { get; set; }

        /// <summary>Категория: Content / Statistics / Finance / Documents / Analytics …</summary>
        public string Category { get; set; } = "";

        /// <summary>Псевдоним/название токена (для удобства)</summary>
        public string? Alias { get; set; }

        /// <summary>Песочница (Sandbox) или боевой контур</summary>
        public bool IsSandbox { get; set; }

        /// <summary>Зашифрованный токен (Protect/Unprotect)</summary>
        public string EncryptedToken { get; set; } = "";

        /// <summary>UTC-время создания/сохранения</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Необязательный Supplier ID (X-Supplier-ID). Нужен, если пользователь имеет доступ к нескольким поставщикам.
        /// Пример: 170896
        /// </summary>
        public long? SupplierId { get; set; }
    }
}
