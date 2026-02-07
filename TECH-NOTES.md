# TECH-NOTES (SellerOps.App)

## Текущее состояние (на момент изменения)

### Хранение и настройки
- Путь к SQLite задаётся в `AppSettings.DatabasePath` (локальный `settings.json`).  
  Если путь не указан — используется `%LocalAppData%\\SellerOps\\sellerops.db`.  
- В коде уже есть предупреждение при выборе облачного пути (YandexDisk/OneDrive/Google Drive).  
- Токены WB сейчас хранятся в SQLite (`ApiTokens`) и шифруются через `SecureStorage` (DPAPI).

### БД и сущности
- EF Core + SQLite. Схема обновляется вручную через `EnsureUpgrade()` в `AppDbContext.Upgrade.cs`.
- Основные таблицы:
  - `WbRealizationLines` (реализационный отчёт)
  - `WbRawFiles` (сырые ответы API)
  - `WbProducts` (товары + себестоимость)
  - `WbImportLogs` (журнал импортов)
  - `WbFbsOrders` (Marketplace FBS)
  - `WbStockSnapshots` (снимки остатков)

### Сервисы API (текущее)
- `WbStatisticsService`: реализационный отчёт, остатки, прочие отчёты.
- `WbCatalogService`: товары (Content API).
- `WbMarketplaceService`: FBS заказы.
- В логике есть retry на 429/5xx, но схема API-клиентов не унифицирована.

### UI
- WPF страницы: `AnalyticsPage`, `ProductsPage`, `MarketplacePage`, `WbSettingsPage`.
- UI использует прямой доступ к `AppDbContext` (без MVVM).
- Графики сейчас отсутствуют; используются таблицы (DataGrid).

## Что планируется менять (в следующих шагах)
1) Настройки:
   - доработать структуру хранения токенов (перенос из SQLite в локальные настройки/cred manager);
   - расширить валидацию пути к БД (если указана папка — автоматически добавлять `sellerops.db`).
2) Синхронизация:
   - унифицировать API-клиентов (throttling/retry/logging);
   - реализовать инкрементальные загрузки через `lastChangeDate`/`rrdId`;
   - завести `SyncState` для каждого источника.
3) Метрики:
   - вынести расчёты KPI в отдельный слой (без "магии" в UI).
4) UI:
   - обновить страницы под "Сводку/По дням/Товары/Заказы/Финансы/Показы".
   - подготовить точки расширения под графики.

