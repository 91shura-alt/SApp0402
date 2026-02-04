using ExcelDataReader;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SellerOps.App.Services
{
    public sealed class CostImportRow
    {
        public long? NmId { get; set; }
        public string? Barcode { get; set; }
        public string? VendorCode { get; set; }
        public decimal Cost { get; set; }
    }

    public static class CostFileParser
    {
        // Заголовки, которые распознаём
        private static readonly string[] NmIdHeaders = { "nmId", "nmid", "nmID", "артикул wb", "артикулwb", "артикул_wb" };
        private static readonly string[] BarcodeHeaders = { "barcode", "штрихкод", "штрих-код", "eans", "ean" };
        private static readonly string[] VendorCodeHeaders = { "vendorcode", "артикул поставщика", "артикулпоставщика", "код поставщика" };
        private static readonly string[] CostHeaders = { "cost", "себестоимость", "закупочная", "закупка" };

        /// <summary>
        /// Читает .csv/.xlsx/.xls и возвращает нормализованные записи.
        /// </summary>
        public static System.Threading.Tasks.Task<List<CostImportRow>> ReadAsync(string path)
            => System.Threading.Tasks.Task.Run(() => Read(path));

        public static List<CostImportRow> Read(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".csv" || ext == ".txt")
                return ReadCsv(path);

            if (ext == ".xlsx" || ext == ".xls")
                return ReadExcel(path);

            // Попробуем как CSV
            return ReadCsv(path);
        }

        // ---------------- CSV ----------------

        private static List<CostImportRow> ReadCsv(string path)
        {
            var lines = File.ReadAllLines(path, DetectEncoding(path));
            if (lines.Length == 0) return new List<CostImportRow>();

            var delim = DetectDelimiter(lines);
            var header = SplitCsvLine(lines[0], delim);
            var map = BuildHeaderMap(header);

            var list = new List<CostImportRow>();
            for (int i = 1; i < lines.Length; i++)
            {
                var row = SplitCsvLine(lines[i], delim);
                var rec = ParseRecord(row, map);
                if (rec != null) list.Add(rec);
            }
            return list;
        }

        private static Encoding DetectEncoding(string path)
        {
            // пробуем UTF8, если BOM нет — пусть будет Win-1251 (русские csv часто так сохранены)
            using var fs = File.OpenRead(path);
            var bom = new byte[4];
            fs.Read(bom, 0, 4);
            fs.Position = 0;
            if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
            return Encoding.GetEncoding(1251); // cp1251
        }

        private static char DetectDelimiter(string[] lines)
        {
            if (lines.Length == 0) return ';';
            var head = lines[0];
            int sc = head.Count(c => c == ';');
            int cc = head.Count(c => c == ',');
            int tc = head.Count(c => c == '\t');
            if (tc >= sc && tc >= cc) return '\t';
            if (sc >= cc) return ';';
            return ',';
        }

        private static string[] SplitCsvLine(string line, char delim)
        {
            // простой CSV: учитываем кавычки
            var res = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        sb.Append('\"'); i++; // экранированная кавычка
                    }
                    else inQuotes = !inQuotes;
                }
                else if (ch == delim && !inQuotes)
                {
                    res.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(ch);
            }
            res.Add(sb.ToString());
            return res.ToArray();
        }

        // ---------------- Excel (ExcelDataReader) ----------------

        private static List<CostImportRow> ReadExcel(string path)
        {
            // Требуются NuGet: ExcelDataReader, ExcelDataReader.DataSet
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Потребуется вызов Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) в App.OnStartup
            using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream);
            var conf = new ExcelDataReader.ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataReader.ExcelDataTableConfiguration
                {
                    UseHeaderRow = true
                }
            };
            var ds = reader.AsDataSet(conf);
            if (ds.Tables.Count == 0) return new List<CostImportRow>();
            var tbl = ds.Tables[0];

            // Построим map по заголовкам
            var header = tbl.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
            var map = BuildHeaderMap(header);

            var list = new List<CostImportRow>();
            foreach (DataRow r in tbl.Rows)
            {
                var row = header.Select(h => r[h]?.ToString() ?? string.Empty).ToArray();
                var rec = ParseRecord(row, map);
                if (rec != null) list.Add(rec);
            }
            return list;
        }

        // ---------------- Общая логика маппинга/парсинга ----------------

        private sealed class HeaderMap
        {
            public int NmId = -1;
            public int Barcode = -1;
            public int VendorCode = -1;
            public int Cost = -1;
        }

        private static HeaderMap BuildHeaderMap(IEnumerable<string> headers)
        {
            var map = new HeaderMap();
            int idx = 0;
            foreach (var h in headers)
            {
                var key = (h ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
                if (map.NmId < 0 && NmIdHeaders.Contains(key)) map.NmId = idx;
                if (map.Barcode < 0 && BarcodeHeaders.Contains(key)) map.Barcode = idx;
                if (map.VendorCode < 0 && VendorCodeHeaders.Contains(key)) map.VendorCode = idx;
                if (map.Cost < 0 && CostHeaders.Contains(key)) map.Cost = idx;
                idx++;
            }
            return map;
        }

        private static CostImportRow? ParseRecord(string[] row, HeaderMap map)
        {
            if (map.Cost < 0) return null; // без цены смысла нет

            long? nmId = null;
            if (map.NmId >= 0 && map.NmId < row.Length)
            {
                var s = row[map.NmId];
                if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                    nmId = v;
            }

            string? barcode = (map.Barcode >= 0 && map.Barcode < row.Length) ? Clean(row[map.Barcode]) : null;
            string? vendor = (map.VendorCode >= 0 && map.VendorCode < row.Length) ? Clean(row[map.VendorCode]) : null;

            if (map.Cost < 0 || map.Cost >= row.Length) return null;
            var costStr = Clean(row[map.Cost]);
            if (!TryParseMoney(costStr, out var cost)) return null;

            // Если вообще нет ключей для сопоставления — пропускаем
            if (!nmId.HasValue && string.IsNullOrEmpty(barcode) && string.IsNullOrEmpty(vendor))
                return null;

            return new CostImportRow
            {
                NmId = nmId,
                Barcode = barcode,
                VendorCode = vendor,
                Cost = cost
            };
        }

        private static string Clean(string? s)
            => (s ?? string.Empty).Trim();

        private static bool TryParseMoney(string s, out decimal value)
        {
            // убираем пробелы-разделители тысяч, заменяем запятую/точку
            var raw = s.Replace(" ", "").Replace("\u00A0", "");
            // пробуем RU (запятая — десятичный), потом Invariant
            if (decimal.TryParse(raw, NumberStyles.Any, new CultureInfo("ru-RU"), out value)) return true;
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;

            // ручная нормализация: приводим к точке как десятичному
            raw = raw.Replace(",", ".");
            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }
}
