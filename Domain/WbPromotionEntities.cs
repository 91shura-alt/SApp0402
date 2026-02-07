using System;

namespace SellerOps.App.Domain
{
    /// <summary>
    /// Промо-акции (Promotion API) в разрезе карточки товара (nmId).
    /// </summary>
    public class WbPromotionItem
    {
        public int Id { get; set; }

        public long NmId { get; set; }
        public long? PromotionId { get; set; }

        public string Name { get; set; } = "";
        public string RequiredDiscount { get; set; } = "";
        public string Status { get; set; } = "";
        public string Details { get; set; } = "";

        public DateTime ImportedAtUtc { get; set; }

        public string RawJson { get; set; } = "";
    }
}
