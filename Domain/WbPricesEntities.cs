using System;

namespace SellerOps.App.Domain
{
    /// <summary>
    /// WB Prices & Discounts API (discounts-prices-api) – агрегат по товару (nmID).
    /// Источник: /api/v2/list/goods и /api/v2/list/goods/filter.
    /// </summary>
    public class WbPriceGood
    {
        public int Id { get; set; }

        public long NmId { get; set; }

        /// <summary>Артикул продавца (vendorCode) из WB.</summary>
        public string VendorCode { get; set; } = "";

        /// <summary>Код валюты ISO 4217 (например 643 для RUB).</summary>
        public int? CurrencyIsoCode4217 { get; set; }

        /// <summary>Скидка продавца, %.</summary>
        public int? Discount { get; set; }

        /// <summary>Скидка WB клуба, %.</summary>
        public int? ClubDiscount { get; set; }

        /// <summary>Признак, что цены задаются на уровне размеров.</summary>
        public bool? EditableSizePrice { get; set; }

        /// <summary>Признак плохой оборачиваемости (isBadTurnover) из API.</summary>
        public bool? IsBadTurnover { get; set; }

        /// <summary>Когда мы импортировали это состояние (UTC).</summary>
        public DateTime ImportedAtUtc { get; set; }

        /// <summary>Сырые данные по товару (JSON) – удобно для отладки/офлайна.</summary>
        public string RawJson { get; set; } = "";
    }

    /// <summary>
    /// WB Prices & Discounts – цены по размерам (sizeID).
    /// </summary>
    public class WbPriceSize
    {
        public int Id { get; set; }

        public long NmId { get; set; }
        public long SizeId { get; set; }

        public string TechSizeName { get; set; } = "";

        public decimal? Price { get; set; }
        public decimal? DiscountedPrice { get; set; }
        public decimal? ClubDiscountedPrice { get; set; }

        public DateTime ImportedAtUtc { get; set; }

        public string RawJson { get; set; } = "";
    }

    /// <summary>
    /// WB Prices & Discounts – товары в карантине (quarantine), если используете этот метод.
    /// </summary>
    public class WbPriceQuarantineGood
    {
        public int Id { get; set; }

        public long NmId { get; set; }
        public string VendorCode { get; set; } = "";

        /// <summary>Причина карантина / сообщение (если отдаётся).</summary>
        public string Reason { get; set; } = "";

        public DateTime ImportedAtUtc { get; set; }

        public string RawJson { get; set; } = "";
    }
}
