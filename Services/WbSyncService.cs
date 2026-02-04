using System;
using System.Threading;

namespace SellerOps.App.Services
{
    public class WbSyncService
    {
        private readonly WbStatisticsService _statSvc = new();
        private Timer? _stocksTimer;
        private Timer? _realTimer;

        public void Start()
        {
            // Остатки: каждые 45 минут
            _stocksTimer = new Timer(async _ =>
            {
                try
                {
                    var dateFrom = DateTime.UtcNow.Date; // или DateTime.UtcNow.Date.AddDays(-1)
                    await _statSvc.SnapshotStocksAsync(dateFrom);
                }
                catch { /* ... */ }

            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(45));

            // Реализации: раз в день за вчера
            _realTimer = new Timer(async _ =>
            {
                try
                {
                    var from = DateTime.UtcNow.Date.AddDays(-1);
                    var to = DateTime.UtcNow.Date;
                    await _statSvc.ImportRealizationByPeriodAsync(from, to, "daily");
                }
                catch { /* лог */ }
            }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));
        }
    }
}
