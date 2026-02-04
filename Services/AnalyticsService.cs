using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using SellerOps.App.Data;

namespace SellerOps.App.Services
{
    public sealed class ProfitSummaryRow
    {
        public long NmId { get; set; }
        public string? Title { get; set; }
        public string? Article { get; set; }

        public int Qty { get; set; }

        // WB поля
        public decimal RevenueToPay { get; set; }          // ppvz_for_pay ("К перечислению")
        public decimal Commission { get; set; }            // ppvz_sales_commission
        public decimal Delivery { get; set; }              // delivery_rub
        public decimal Storage { get; set; }               // storage_fee
        public decimal Penalties { get; set; }             // penalty
        public decimal Deductions { get; set; }            // deduction

        // Производные
        public decimal DirectWBFees { get; set; }          // комиссии/логистика/хранение/штрафы/удержания (для справки)

        // Ваши добавки (пока заглушки)
        public decimal Cost { get; set; }                  // себестоимость
        public decimal Prep { get; set; }                  // подготовка/упаковка
        public decimal Ads { get; set; }                   // реклама (если нужно)

        public decimal NetProfit { get; set; }             // "К перечислению" - Cost - Prep - Ads
        public decimal MarginPct { get; set; }             // (NetProfit / RevenueToPay) * 100
    }

    public class AnalyticsService
    {
        private readonly AppDbContext _db;
        public AnalyticsService(AppDbContext? db = null) => _db = db ?? AppDbContext.Instance;

        public IEnumerable<ProfitSummaryRow> BuildProfitSummary(DateTime from, DateTime to)
        {
            // чтобы "ToDate" считалась включительно по дню
            var fromDate = from.Date;
            var toExclusive = to.Date.AddDays(1);

            // Подтягиваем справочник товаров (для названия/артикула/себестоимости)
            var productMap = _db.WbProducts
                .AsNoTracking()
                .ToDictionary(p => p.NmId);

            // ВАЖНО: забираем строки в память, чтобы Sum(decimal) не переводился в SQL
            var rows = _db.WbRealizationLines
                .AsNoTracking()
                .Where(x => x.RrDt >= fromDate && x.RrDt < toExclusive)
                .Select(x => new
                {
                    x.NmId,
                    x.Quantity,
                    x.PpvzForPay,
                    x.PpvzSalesCommission,
                    x.DeliveryRub,
                    x.StorageFee,
                    x.Penalty,
                    x.Deduction
                })
                .ToList();

            var result = rows
                .GroupBy(x => x.NmId)
                .Select(g =>
                {
                    var nmId = g.Key;
                    productMap.TryGetValue(nmId, out var p);

                    var qty = g.Sum(r => r.Quantity);

                    var revenue = g.Sum(r => r.PpvzForPay);
                    var commission = g.Sum(r => r.PpvzSalesCommission);
                    var delivery = g.Sum(r => r.DeliveryRub);
                    var storage = g.Sum(r => r.StorageFee);
                    var penalties = g.Sum(r => r.Penalty);
                    var deductions = g.Sum(r => r.Deduction);

                    var directFees = commission + delivery + storage + penalties + deductions;

                    // Себестоимость берём из WbProducts.Cost (если заполнено), как цена за 1 шт.
                    var unitCost = p?.Cost ?? 0m;
                    var cost = unitCost * qty;

                    var prep = 0m; // TODO: сюда подключим вашу "подготовку"
                    var ads = 0m;  // TODO: сюда подключим рекламу

                    var net = revenue - cost - prep - ads;
                    var margin = revenue == 0m ? 0m : Math.Round(net / revenue * 100m, 1);

                    return new ProfitSummaryRow
                    {
                        NmId = nmId,
                        Title = p?.Title,
                        Article = p?.VendorCode ?? p?.Article,

                        Qty = qty,

                        RevenueToPay = revenue,
                        Commission = commission,
                        Delivery = delivery,
                        Storage = storage,
                        Penalties = penalties,
                        Deductions = deductions,

                        DirectWBFees = directFees,

                        Cost = cost,
                        Prep = prep,
                        Ads = ads,

                        NetProfit = net,
                        MarginPct = margin
                    };
                })
                .OrderByDescending(x => x.NetProfit)
                .ToList();

            return result;
        }
    }
}
