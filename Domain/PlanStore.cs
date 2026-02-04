using System;

namespace SellerOps.App.Domain
{
    /// <summary>Хранит текущее состояние выбора поставки/короба/режима.</summary>
    public static class PlanStore
    {
        public static Guid? CurrentSupplyId { get; set; }
        public static string? ActiveBoxCode { get; set; }
        /// <summary>add | remove | ship</summary>
        public static string Mode { get; set; } = "add";
    }
}
