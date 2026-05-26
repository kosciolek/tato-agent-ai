using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace AgentReadonly.Services
{
    public class UsageLedger
    {
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly object sync = new object();

        public UsageSummary LoadSummary()
        {
            lock (sync)
            {
                List<UsageEntry> entries = LoadEntries();
                UsageSummary summary = Summarize(entries);
                AppLog.Info("Usage summary loaded: entries=" + entries.Count +
                    " today_usd=" + summary.TodayUsd.ToString("0.000000", CultureInfo.InvariantCulture) +
                    " last30d_usd=" + summary.Last30DaysUsd.ToString("0.000000", CultureInfo.InvariantCulture) +
                    " has_unpriced_usage=" + summary.HasUnpricedUsage);
                return summary;
            }
        }

        public UsageSummary Add(UsageEntry entry)
        {
            lock (sync)
            {
                List<UsageEntry> entries = LoadEntries();
                entries.Add(entry);
                SaveEntries(entries);
                UsageSummary summary = Summarize(entries);
                AppLog.Info("Usage entry persisted: path=" + AppPaths.UsagePath +
                    " entries=" + entries.Count +
                    " model=" + entry.Model +
                    " cost_usd=" + entry.CostUsd.ToString("0.000000", CultureInfo.InvariantCulture) +
                    " price_known=" + entry.PriceKnown);
                return summary;
            }
        }

        public static UsageEntry FromResponse(string model, Dictionary<string, object> response)
        {
            Dictionary<string, object> usage = GetMap(response, "usage");
            int inputTokens = GetInt(usage, "input_tokens");
            int outputTokens = GetInt(usage, "output_tokens");
            int cachedInputTokens = GetInt(GetMap(usage, "input_tokens_details"), "cached_tokens");
            int webSearchCalls = CountOutputItems(response, "web_search_call");

            UsageEntry entry = new UsageEntry();
            entry.CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            entry.Model = model ?? "";
            entry.InputTokens = inputTokens;
            entry.CachedInputTokens = Math.Max(0, Math.Min(cachedInputTokens, inputTokens));
            entry.OutputTokens = outputTokens;
            entry.WebSearchCalls = webSearchCalls;
            entry.PriceKnown = TryCalculateCost(entry, out double cost);
            entry.CostUsd = cost;
            return entry;
        }

        private UsageSummary Summarize(List<UsageEntry> entries)
        {
            DateTime now = DateTime.Now;
            DateTime today = now.Date;
            DateTime last30Days = now.AddDays(-30);

            UsageSummary summary = new UsageSummary();
            foreach (UsageEntry entry in entries)
            {
                DateTime created;
                if (!TryParseLocal(entry.CreatedAtUtc, out created))
                    continue;

                if (created >= today)
                    summary.TodayUsd += entry.CostUsd;
                if (created >= last30Days)
                {
                    summary.Last30DaysUsd += entry.CostUsd;
                    if (!entry.PriceKnown && HasUsage(entry))
                        summary.HasUnpricedUsage = true;
                }
            }

            return summary;
        }

        private List<UsageEntry> LoadEntries()
        {
            try
            {
                if (!File.Exists(AppPaths.UsagePath))
                    return new List<UsageEntry>();

                string json = File.ReadAllText(AppPaths.UsagePath);
                List<UsageEntry> entries = serializer.Deserialize<List<UsageEntry>>(json);
                return entries ?? new List<UsageEntry>();
            }
            catch
            {
                AppLog.Warn("Usage ledger load failed, starting with empty summary: path=" + AppPaths.UsagePath);
                return new List<UsageEntry>();
            }
        }

        private void SaveEntries(List<UsageEntry> entries)
        {
            string dir = Path.GetDirectoryName(AppPaths.UsagePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = serializer.Serialize(entries);
            File.WriteAllText(AppPaths.UsagePath, json);
        }

        private static bool TryCalculateCost(UsageEntry entry, out double cost)
        {
            ModelPrice price;
            if (!TryGetPrice(entry.Model, out price))
            {
                cost = 0.0;
                return false;
            }

            int cachedTokens = Math.Max(0, Math.Min(entry.CachedInputTokens, entry.InputTokens));
            int uncachedInputTokens = Math.Max(0, entry.InputTokens - cachedTokens);

            cost =
                (uncachedInputTokens / 1000000.0 * price.InputPerMillion) +
                (cachedTokens / 1000000.0 * price.CachedInputPerMillion) +
                (entry.OutputTokens / 1000000.0 * price.OutputPerMillion) +
                (entry.WebSearchCalls * 0.01);
            return true;
        }

        private static bool HasUsage(UsageEntry entry)
        {
            return entry.InputTokens > 0 || entry.OutputTokens > 0 || entry.WebSearchCalls > 0;
        }

        private static bool TryGetPrice(string model, out ModelPrice price)
        {
            string normalized = (model ?? "").Trim().ToLowerInvariant();

            if (normalized.StartsWith("gpt-5.5", StringComparison.Ordinal))
            {
                price = new ModelPrice(5.00, 0.50, 30.00);
                return true;
            }

            if (normalized.StartsWith("gpt-5.4-mini", StringComparison.Ordinal))
            {
                price = new ModelPrice(0.75, 0.075, 4.50);
                return true;
            }

            if (normalized.StartsWith("gpt-5.4", StringComparison.Ordinal))
            {
                price = new ModelPrice(2.50, 0.25, 15.00);
                return true;
            }

            price = new ModelPrice(0, 0, 0);
            return false;
        }

        private static bool TryParseLocal(string text, out DateTime local)
        {
            DateTimeOffset offset;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out offset))
            {
                local = offset.LocalDateTime;
                return true;
            }

            local = DateTime.MinValue;
            return false;
        }

        private static Dictionary<string, object> GetMap(Dictionary<string, object> map, string name)
        {
            if (map == null)
                return new Dictionary<string, object>();

            object value;
            if (!map.TryGetValue(name, out value) || value == null)
                return new Dictionary<string, object>();

            Dictionary<string, object> child = value as Dictionary<string, object>;
            return child ?? new Dictionary<string, object>();
        }

        private static int GetInt(Dictionary<string, object> map, string name)
        {
            object value;
            if (map == null || !map.TryGetValue(name, out value) || value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static int CountOutputItems(Dictionary<string, object> response, string typeName)
        {
            object[] output = GetArray(response, "output");
            int count = 0;
            foreach (object rawItem in output)
            {
                Dictionary<string, object> item = rawItem as Dictionary<string, object>;
                if (item != null && string.Equals(GetString(item, "type"), typeName, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static object[] GetArray(Dictionary<string, object> map, string name)
        {
            if (map == null)
                return new object[0];

            object value;
            if (!map.TryGetValue(name, out value) || value == null)
                return new object[0];

            object[] array = value as object[];
            return array ?? new[] { value };
        }

        private static string GetString(Dictionary<string, object> map, string name)
        {
            object value;
            return map.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private struct ModelPrice
        {
            public ModelPrice(double inputPerMillion, double cachedInputPerMillion, double outputPerMillion)
            {
                InputPerMillion = inputPerMillion;
                CachedInputPerMillion = cachedInputPerMillion;
                OutputPerMillion = outputPerMillion;
            }

            public double InputPerMillion { get; private set; }
            public double CachedInputPerMillion { get; private set; }
            public double OutputPerMillion { get; private set; }
        }
    }

    public class UsageEntry
    {
        public string CreatedAtUtc { get; set; }
        public string Model { get; set; }
        public int InputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int WebSearchCalls { get; set; }
        public bool PriceKnown { get; set; }
        public double CostUsd { get; set; }
    }

    public class UsageSummary
    {
        public double TodayUsd { get; set; }
        public double Last30DaysUsd { get; set; }
        public bool HasUnpricedUsage { get; set; }
    }
}
