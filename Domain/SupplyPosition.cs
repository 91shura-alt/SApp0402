using System;

namespace SellerOps.App.Domain
{
    /// <summary>
    /// Позиция для планирования/подбора (если сейчас не используешь — всё равно пусть будет,
    /// чтобы DbContext не ссылался на несуществующий тип).
    /// </summary>
    public class SupplyPosition
    {
        public int Id { get; set; }

        public string Mp { get; set; } = "";          // WB / Ozon / etc
        public string Article { get; set; } = "";
        public string? Barcode { get; set; }
        public string? Warehouse { get; set; }

        public int Need { get; set; }
        public int Picked { get; set; }

        public string? Status { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
