using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SellerOps.App.Domain
{
    /// <summary>
    /// Карточка товара WB (срез, который мы храним локально).
    /// Заполняется через WbCatalogService при синхронизации контента.
    /// </summary>
    public class WbProduct
    {
        /// <summary>Идентификатор карточки в WB (nmID). Используем как PK.</summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long NmId { get; set; }

        /// <summary>Название карточки (title).</summary>
        [MaxLength(500)]
        public string Title { get; set; } = "";

        /// <summary>Бренд.</summary>
        [MaxLength(200)]
        public string? Brand { get; set; }

        /// <summary>Предмет/категория (subjectName).</summary>
        [MaxLength(200)]
        public string? Subject { get; set; }

        /// <summary>Артикул продавца (иногда в ответах идёт как supplierArticle/article).</summary>
        [MaxLength(200)]
        public string? Article { get; set; }

        /// <summary>vendorCode (артикул на стороне WB-интерфейсов контента).</summary>
        [MaxLength(200)]
        public string? VendorCode { get; set; }

        /// <summary>Штрихкод (берём первый попавшийся из массива barcodes).</summary>
        [MaxLength(100)]
        public string? Barcode { get; set; }

        /// <summary>Флаг архивации карточки (если API вернуло archived = true).</summary>
        public bool IsArchived { get; set; }

        /// <summary>UTC-время последней успешной синхронизации этой записи.</summary>
        public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
        // --- габариты/вес/объём ---
        public double? LengthCm { get; set; }
        public double? WidthCm { get; set; }
        public double? HeightCm { get; set; }
        public double? WeightKg { get; set; }
        public double? VolumeM3 { get; set; } // вычисляемое (Д×Ш×В)/1_000_000

        // --- себестоимость (для импорта) ---
        public decimal? Cost { get; set; }
        public string? BarcodesJson { get; set; }
        public string? RawJson { get; set; }
    }

    /// <summary>
    /// Строка отчёта Реализации (Statistics API). 
    /// Нужна для дальнейшей аналитики (валовая выручка, комиссии, логистика и пр.).
    /// Создаётся в WbStatisticsService.
    /// </summary>
    public class WbRealizationLine
    {
        /// <summary>Уникальный идентификатор строки отчёта (rrd_id) — используем как PK.</summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long RrdId { get; set; }
        public string TechSize { get; set; } = "";
        public DateTime? CancelDt { get; set; }

        /// <summary>Дата операции (rr_dt).</summary>
        public DateTime? RrDt { get; set; }

        /// <summary>Дата/время заказа (order_dt). WB обычно отдаёт в UTC (суффикс Z).</summary>
        public DateTime? OrderDt { get; set; }

        /// <summary>Дата продажи (sale_dt).</summary>
        public DateTime? SaleDt { get; set; }

        /// <summary>Дата создания строки отчёта (create_dt).</summary>
        public DateTime? CreateDt { get; set; }

        /// <summary>Идентификатор карточки WB (nm_id).</summary>
        public long NmId { get; set; }

        /// <summary>Штрихкод.</summary>
        [MaxLength(100)]
        public string Barcode { get; set; } = "";

        /// <summary>Артикул продавца (supplierArticle).</summary>
        [MaxLength(200)]
        public string SupplierArticle { get; set; } = "";

        /// <summary>Склад, с которого произошла операция.</summary>
        [MaxLength(200)]
        public string WarehouseName { get; set; } = "";

        /// <summary>Номер заказа/отгрузки (srid) — полезен для сверок.</summary>
        [MaxLength(50)]
        public string Srid { get; set; } = "";

        /// <summary>Тип документа (doc_type_name).</summary>
        [MaxLength(200)]
        public string DocTypeName { get; set; } = "";

        /// <summary>Тип операции (supplier_oper_name).</summary>
        [MaxLength(200)]
        public string SupplierOperName { get; set; } = "";

        /// <summary>Количество.</summary>
        public int Quantity { get; set; }

        /// <summary>Цена с учётом скидки, руб.</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal PriceWithDiscRub { get; set; }

        /// <summary>К перечислению (ppvz_for_pay).</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal PpvzForPay { get; set; }

        /// <summary>Комиссия (ppvz_sales_commission).</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal PpvzSalesCommission { get; set; }

        /// <summary>Доставка (delivery_rub).</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal DeliveryRub { get; set; }

        /// <summary>Хранение (storage_fee).</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StorageFee { get; set; }

        /// <summary>Прочие удержания (deduction).</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Deduction { get; set; }

        /// <summary>Штраф (penalty).</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Penalty { get; set; }
        public string? RawJson { get; set; }
    }

    /// <summary>
    /// Снимок остатков по складам (Statistics API: stocks).
    /// Создаётся в WbStatisticsService.
    /// </summary>
    public class WbStockSnapshot
    {
        public int Id { get; set; }

        public DateTime SnapshotAt { get; set; }

        // базовые поля (мы их всегда заполняем)
        public string Warehouse { get; set; } = "";
        public long NmId { get; set; }
        public string Barcode { get; set; } = "";
        public int Quantity { get; set; }

        // расширенные поля (могут быть NULL в старых строках/не всегда приходят)
        public string? SupplierArticle { get; set; }
        public string? TechSize { get; set; }
        public string? Subject { get; set; }
        public string? Category { get; set; }
        public string? Brand { get; set; }
        public string? AddressFull { get; set; }

        public decimal? Price { get; set; }
        public int? Discount { get; set; }

        public int? QuantityFull { get; set; }
        public int? InWayToClient { get; set; }
        public int? InWayFromClient { get; set; }

        public bool? IsSupply { get; set; }
        public bool? IsRealization { get; set; }

        public string? Code { get; set; }
        public string? SCCode { get; set; }

        public DateTime? LastChangeDate { get; set; }
        public string? RawJson { get; set; }
    }


    /// <summary>
    /// Баланс/остаток в разрезе товаров или дат — заглушка под будущую аналитику.
    /// В проекте используются только ссылки на DbSet, поэтому минимальная модель достаточно.
    /// </summary>
    public class WbBalance
    {
        [Key]
        public int Id { get; set; }

        /// <summary>NmId товара (если привязываем к карточке).</summary>
        public long? NmId { get; set; }

        /// <summary>Дата периода баланса (если ведём по дням).</summary>
        public DateTime? Period { get; set; }

        /// <summary>Количество/сумма — поле под дальнейшую детализацию.</summary>
        public decimal? Amount { get; set; }
    }

    /// <summary>
    /// Хранилище «сырых» файлов/ответов WB для диагностики и повторной обработки:
    /// JSON, CSV, XLSX и пр. Удобно хранить копии выгрузок.
    /// </summary>
    public class WbRawFile
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Тип содержимого (например: realization, stocks, orders, prices…)</summary>
        [MaxLength(100)]
        public string Kind { get; set; } = "";

        /// <summary>Имя файла / идентификатор выгрузки.</summary>
        [MaxLength(260)]
        public string FileName { get; set; } = "";

        /// <summary>UTC-время добавления.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Сырые данные (обычно JSON-тело ответа WB).</summary>
        public string? Json { get; set; }
    }

    /// <summary>
    /// Журнал импортов (нужно, чтобы не дергать WB API заново, если день уже полностью загружен).
    /// </summary>
    public class WbImportLog
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Тип импорта: realizations, stocks и т.д.</summary>
        [MaxLength(50)]
        public string Kind { get; set; } = "";

        /// <summary>День, за который импортировали данные (локальная дата).</summary>
        public DateTime Day { get; set; }

        /// <summary>UTC-время, когда импорт завершился успешно.</summary>
        public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Признак, что импорт дня полностью завершён (последняя страница получена).</summary>
        public bool IsComplete { get; set; }

        /// <summary>Сколько строк добавили в базу за этот импорт.</summary>
        public int AddedRows { get; set; }

        /// <summary>Последний rrd_id, который получили.</summary>
        public long MaxRrdId { get; set; }
    }
    public class WbFbsOrder
    {
        public long Id { get; set; }
        public long OrderId { get; set; }
        public string? OrderUid { get; set; }
        public string? Rid { get; set; }
        public string? SupplyId { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Article { get; set; }
        public long? NmId { get; set; }
        public long? ChrtId { get; set; }
        public long? WarehouseId { get; set; }
        public long? OfficeId { get; set; }
        public int? Price { get; set; }
        public int? FinalPrice { get; set; }
        public int? CurrencyCode { get; set; }
        public string? DeliveryType { get; set; }
        public string? Comment { get; set; }

        // было
        public string RawJson { get; set; } = "";
        public DateTime ImportedAtUtc { get; set; }
        public string? Status { get; set; }

        // ✅ добавь
        public string? SupplierStatus { get; set; }
        public string? WbStatus { get; set; }

        public string? OfficesJson { get; set; }
        public string? SkusJson { get; set; }

        public int? ConvertedPrice { get; set; }
        public int? ConvertedFinalPrice { get; set; }
        public int? ConvertedCurrencyCode { get; set; }

        public int? CargoType { get; set; }
        public bool? IsZeroOrder { get; set; }

        public int? SalePrice { get; set; }
        public int? ScanPrice { get; set; }

        public DateTime? Ddate { get; set; }
        public DateTime? SellerDate { get; set; }
        public string? ColorCode { get; set; }

        public string? AddressFull { get; set; }
        public double? AddressLatitude { get; set; }
        public double? AddressLongitude { get; set; }

        public string? RequiredMetaJson { get; set; }
        public string? OptionalMetaJson { get; set; }
        public bool? IsB2B { get; set; }
    }


}
