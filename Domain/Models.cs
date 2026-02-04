using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SellerOps.App.Domain
{
    public class Supply
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Number { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<SupplyItem> Items { get; set; } = new List<SupplyItem>();
        public virtual ICollection<ScanLog> Logs { get; set; } = new List<ScanLog>();
        public virtual ICollection<Box> Boxes { get; set; } = new List<Box>();
    }

    public class SupplyItem
    {
        public int Id { get; set; }
        public Guid SupplyId { get; set; }
        public virtual Supply Supply { get; set; } = null!;

        public string MP { get; set; } = "";
        public string Name { get; set; } = "";
        public string Article { get; set; } = "";
        public string Barcode { get; set; } = "";
        public string Warehouse { get; set; } = ""; // CFO | Samara | YUG
        public int Need { get; set; }
        public int QtyPicked { get; set; }
        public string Status { get; set; } = "Ожидает"; // Ожидает | Собран | Отгружен
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // совместимость со старым кодом
        [NotMapped] public string WarehouseCode => Warehouse;
        [NotMapped] public int Needs { get => Need; set => Need = value; }
        [NotMapped] public int Picked { get => QtyPicked; set => QtyPicked = value; }
        [NotMapped] public int Remaining => Math.Max(0, Need - QtyPicked);
        [NotMapped] public int Total => Need;
        [NotMapped] public string State { get => Status; set => Status = value; }
    }

    public class ScanLog
    {
        public long Id { get; set; }
        public Guid SupplyId { get; set; }
        public DateTime Ts { get; set; } = DateTime.Now;
        public string Kind { get; set; } = "";  // info/warn/error/scan/ship/box
        public string Message { get; set; } = "";
        public string? Barcode { get; set; }
        public string? Warehouse { get; set; }
        public string? BoxCode { get; set; }
        public int Delta { get; set; }
    }

    // --- Новое: учёт коробов и их содержимого ---

    public class Box
    {
        public long Id { get; set; }
        public Guid SupplyId { get; set; }
        public virtual Supply Supply { get; set; } = null!;
        public string Code { get; set; } = "";            // WB_CFO1, Ozon_UG1 ...
        public string Status { get; set; } = "Нет";       // Нет|Открыт|Закрыт|Отгружен
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<BoxItem> Items { get; set; } = new List<BoxItem>();
    }

    public class BoxItem
    {
        public long Id { get; set; }
        public long BoxId { get; set; }
        public virtual Box Box { get; set; } = null!;

        public int SupplyItemId { get; set; }
        public virtual SupplyItem SupplyItem { get; set; } = null!;

        public int Quantity { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
