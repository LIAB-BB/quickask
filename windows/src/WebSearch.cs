using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace QuickAskWin
{
    public class SearchItem
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Snippet { get; set; }
        public string Date { get; set; }
        public double Score { get; set; }

        public SearchItem()
        {
            Title = "";
            Url = "";
            Snippet = "";
            Date = "";
            Score = 0;
        }
    }

    /// <summary>Web search with two providers: Bing RSS (free) and DeepSeek Search API.</summary>
    public static class WebSearch
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

        private static readonly HttpClient Http = CreateClient();
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        static WebSearch()
        {
            // Some Windows TLS stacks still negotiate down by default; Bing resets
            // those handshakes. Pin TLS 1.2 before the first request.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;
        }

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = true;
            handler.UseProxy = false;
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            HttpClient client = new HttpClient(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        public static bool IsDeepSeek(AppConfig cfg)
        {
            string provider = (cfg.SearchProvider ?? "bing").Trim().ToLowerInvariant();
            return provider == "deepseek" || provider == "deepseek-api" || provider == "deepseek-search-api";
        }

        public static async Task<List<SearchItem>> SearchAsync(
            string query, string originalQuestion, AppConfig cfg, CancellationToken ct)
        {
            int max = cfg.SearchMaxResults > 0 ? cfg.SearchMaxResults : 5;
            if (IsDeepSeek(cfg)) return await DeepSeekAsync(query, originalQuestion, cfg, max, ct);

            string q = (query ?? "").Trim();
            if (q.Length == 0) throw new ChatException("搜索词为空");
            return await BingAsync(q, cfg, max, ct);
        }

        // ── DeepSeek Search API ───────────────────────────────────────────────

        /// <summary>
        /// POST {base}/search with {query,count,need_content,ori_question}; the key falls
        /// back to the chat key so one DeepSeek key can cover both when billing allows it.
        /// </summary>
        private static async Task<List<SearchItem>> DeepSeekAsync(
            string query, string originalQuestion, AppConfig cfg, int max, CancellationToken ct)
        {
            string key = !string.IsNullOrEmpty(cfg.SearchApiKey) ? cfg.SearchApiKey : cfg.ApiKey;
            if (string.IsNullOrEmpty(key))
                throw new ChatException("DeepSeek 搜索缺少 Key：在「设置 → 搜索 Key」里填写，或切回 Bing");

            string baseUrl = string.IsNullOrEmpty(cfg.SearchBaseURL)
                ? "https://api.deepseek.com/v1/tools"
                : cfg.SearchBaseURL.TrimEnd('/');
            string url = baseUrl + "/search";

            string q = (query ?? "").Trim();
            if (q.Length == 0) throw new ChatException("搜索词为空");
            if (q.Length > 128) q = q.Substring(0, 128);

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["query"] = q;
            // Price is per request, not per result: fetch a wider net, then keep the
            // best-scored `max` items for the prompt.
            body["count"] = Math.Min(50, Math.Max(max, 8));
            body["need_content"] = false;
            if (!string.IsNullOrEmpty(originalQuestion))
            {
                string original = originalQuestion.Trim();
                if (original.Length > 128) original = original.Substring(0, 128);
                body["ori_question"] = original;
            }

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(Ser.Serialize(body), Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                req.Headers.TryAddWithoutValidation("User-Agent", "QuickAskWin/1.0");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                int timeoutMs = cfg.SearchTimeoutMs > 0 ? cfg.SearchTimeoutMs : 8000;
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(timeoutMs);
                    using (HttpResponseMessage resp = await Http
                        .SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token)
                        .ConfigureAwait(false))
                    {
                        string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if ((int)resp.StatusCode != 200)
                            throw new ChatException("DeepSeek 搜索 HTTP " + (int)resp.StatusCode + "：" + ApiError(raw));
                        return ParseDeepSeek(raw, max, cfg.SearchMinScore);
                    }
                }
            }
        }

        private static List<SearchItem> ParseDeepSeek(string raw, int max, double minScore)
        {
            List<SearchItem> list = new List<SearchItem>();
            Dictionary<string, object> obj;
            try { obj = Ser.Deserialize<Dictionary<string, object>>(raw); }
            catch { throw new ChatException("DeepSeek 搜索返回了无法解析的内容"); }
            if (obj == null) return list;

            object resultsObj;
            if (!obj.TryGetValue("results", out resultsObj)) return list;
            IList results = resultsObj as IList;
            if (results == null) return list;

            foreach (object entry in results)
            {
                Dictionary<string, object> row = entry as Dictionary<string, object>;
                if (row == null) continue;
                string title = Str(row, "title");
                string link = Str(row, "url");
                if (title.Length == 0 || link.Length == 0) continue;
                if (!link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

                string excerpt = Str(row, "summary");
                if (excerpt.Length == 0) excerpt = Str(row, "snippet");
                if (excerpt.Length > 600) excerpt = excerpt.Substring(0, 600) + "…";

                SearchItem item = new SearchItem();
                item.Title = title;
                item.Url = link;
                item.Snippet = excerpt;
                item.Date = FormatDate(Str(row, "date"));
                double score;
                object scoreObj;
                if (row.TryGetValue("rerank_score", out scoreObj) && scoreObj != null
                    && double.TryParse(Convert.ToString(scoreObj), NumberStyles.Float, CultureInfo.InvariantCulture, out score))
                    item.Score = score;
                list.Add(item);
            }

            bool scored = false;
            foreach (SearchItem item in list) if (item.Score > 0) { scored = true; break; }
            if (scored)
            {
                if (minScore > 0) list.RemoveAll(delegate(SearchItem item) { return item.Score < minScore; });
                list.Sort(delegate(SearchItem a, SearchItem b) { return b.Score.CompareTo(a.Score); });
            }
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        private static string FormatDate(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            DateTime parsed;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out parsed))
                return parsed.ToLocalTime().ToString("yyyy-MM-dd");
            return raw.Length <= 32 ? raw : raw.Substring(0, 32);
        }

        private static string Str(Dictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) return "";
            string text = Convert.ToString(value);
            return (text ?? "").Trim();
        }

        private static string ApiError(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "（空响应）";
            try
            {
                Dictionary<string, object> obj = Ser.Deserialize<Dictionary<string, object>>(raw);
                if (obj != null)
                {
                    object errObj;
                    if (obj.TryGetValue("error", out errObj) && errObj != null)
                    {
                        Dictionary<string, object> err = errObj as Dictionary<string, object>;
                        if (err != null && err.ContainsKey("message") && err["message"] != null)
                            return Convert.ToString(err["message"]);
                        string direct = Convert.ToString(errObj);
                        if (!string.IsNullOrEmpty(direct)) return direct;
                    }
                    object msg;
                    if (obj.TryGetValue("message", out msg) && msg != null) return Convert.ToString(msg);
                }
            }
            catch { }
            return raw.Length > 300 ? raw.Substring(0, 300) : raw;
        }

        // ── Bing RSS (free fallback) ──────────────────────────────────────────

        private static async Task<List<SearchItem>> BingAsync(string q, AppConfig cfg, int max, CancellationToken ct)
        {
            int timeoutMs = cfg.SearchTimeoutMs > 0 ? cfg.SearchTimeoutMs : 8000;
            string url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(q)
                + "&format=rss&count=" + max + "&setlang=zh-CN";

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept", "application/rss+xml, application/xml;q=0.9, */*;q=0.8");

                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(timeoutMs);
                    using (HttpResponseMessage resp = await Http
                        .SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token)
                        .ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            throw new ChatException("搜索返回 HTTP " + (int)resp.StatusCode);
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return ParseBing(body, max);
                    }
                }
            }
        }

        private static List<SearchItem> ParseBing(string xml, int max)
        {
            List<SearchItem> list = new List<SearchItem>();
            if (string.IsNullOrEmpty(xml)) return list;
            string head = xml.TrimStart();
            if (head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                throw new ChatException("搜索被拦截，返回了网页而不是结果");

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { throw new ChatException("搜索返回格式异常"); }

            foreach (XElement item in doc.Descendants("item"))
            {
                string title = Clean((string)item.Element("title"));
                string link = ((string)item.Element("link") ?? "").Trim();
                string snippet = Clean((string)item.Element("description"));
                if (title.Length == 0 || link.Length == 0) continue;
                if (!link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

                SearchItem result = new SearchItem();
                result.Title = title;
                result.Url = link;
                result.Snippet = snippet.Length > 400 ? snippet.Substring(0, 400) + "…" : snippet;
                list.Add(result);
                if (list.Count >= max) break;
            }
            return list;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string text = WebUtility.HtmlDecode(value);
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = Regex.Replace(text, "\\s+", " ");
            return text.Trim();
        }
    }
}
