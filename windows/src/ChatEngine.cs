using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QuickAskWin
{
    public class ChatException : Exception
    {
        public ChatException(string message) : base(message) { }
    }

    /// <summary>OpenAI-compatible streaming client with SSE parsing.</summary>
    public static class ChatEngine
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = true;
            // Direct connection by default: api.deepseek.com is reachable without the
            // local proxy, and a dead system proxy would otherwise break every request.
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        public static string Endpoint(string baseURL)
        {
            string b = (baseURL ?? "").Trim();
            while (b.EndsWith("/")) b = b.Substring(0, b.Length - 1);
            if (b.Length == 0) throw new ChatException("还没填 API 地址，点右上角「设置」配置一下");
            if (!b.EndsWith("/chat/completions")) b += "/chat/completions";
            return b;
        }

        /// <summary>Stream one completion; onDelta is called on a worker thread.</summary>
        public static async Task StreamAsync(
            AppConfig cfg,
            List<Dictionary<string, object>> messages,
            Action<string> onDelta,
            CancellationToken ct)
        {
            string url = Endpoint(cfg.BaseURL);

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = cfg.Model;
            body["messages"] = messages;
            body["stream"] = true;
            body["max_tokens"] = cfg.MaxTokens;
            string effort = cfg.Effort();
            if (effort != null) body["reasoning_effort"] = effort;

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(Ser.Serialize(body), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
            req.Headers.TryAddWithoutValidation("User-Agent", "QuickAskWin/1.0");
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            if (cfg.ExtraHeaders != null)
            {
                foreach (KeyValuePair<string, string> kv in cfg.ExtraHeaders)
                {
                    string value = kv.Value == "auto" ? Guid.NewGuid().ToString() : kv.Value;
                    req.Headers.TryAddWithoutValidation(kv.Key, value);
                }
            }

            HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            using (resp)
            using (ct.Register(delegate { try { resp.Dispose(); } catch { } }))
            {
                if ((int)resp.StatusCode != 200)
                {
                    string raw = "";
                    try { raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                    catch { }
                    throw new ChatException("HTTP " + (int)resp.StatusCode + "：" + Friendly(raw));
                }

                using (Stream stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (!line.StartsWith("data:")) continue;
                        string payload = line.Substring(5).Trim();
                        if (payload.Length == 0) continue;
                        if (payload == "[DONE]") break;

                        string content = Extract(payload);
                        if (!string.IsNullOrEmpty(content)) onDelta(content);
                    }
                }
            }
        }

        /// <summary>One non-streaming completion, used for the search-query rewrite.</summary>
        public static async Task<string> CompleteAsync(
            AppConfig cfg,
            List<Dictionary<string, object>> messages,
            CancellationToken ct)
        {
            string url = Endpoint(cfg.BaseURL);

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = cfg.Model;
            body["messages"] = messages;
            body["stream"] = false;
            body["max_tokens"] = cfg.MaxTokens;
            string effort = cfg.Effort();
            if (effort != null) body["reasoning_effort"] = effort;

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(Ser.Serialize(body), Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
                req.Headers.TryAddWithoutValidation("User-Agent", "QuickAskWin/1.0");
                if (cfg.ExtraHeaders != null)
                {
                    foreach (KeyValuePair<string, string> kv in cfg.ExtraHeaders)
                    {
                        string value = kv.Value == "auto" ? Guid.NewGuid().ToString() : kv.Value;
                        req.Headers.TryAddWithoutValidation(kv.Key, value);
                    }
                }

                using (HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if ((int)resp.StatusCode != 200)
                        throw new ChatException("HTTP " + (int)resp.StatusCode + "：" + Friendly(raw));
                    return ParseMessage(raw);
                }
            }
        }

        private static string ParseMessage(string raw)
        {
            Dictionary<string, object> obj;
            try { obj = Ser.Deserialize<Dictionary<string, object>>(raw); }
            catch { throw new ChatException("接口返回了无法解析的内容"); }
            if (obj == null) throw new ChatException("接口返回为空");

            object choicesObj;
            if (!obj.TryGetValue("choices", out choicesObj)) throw new ChatException("接口没有返回 choices");
            IList choices = choicesObj as IList;
            if (choices == null || choices.Count == 0) throw new ChatException("接口没有返回候选回答");
            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null) throw new ChatException("接口返回结构异常");
            object messageObj;
            if (!first.TryGetValue("message", out messageObj)) return "";
            Dictionary<string, object> message = messageObj as Dictionary<string, object>;
            if (message == null) return "";
            object content;
            if (!message.TryGetValue("content", out content) || content == null) return "";
            return content as string ?? "";
        }

        private static string Extract(string payload)        {
            Dictionary<string, object> obj;
            try { obj = Ser.Deserialize<Dictionary<string, object>>(payload); }
            catch { return null; }
            if (obj == null) return null;

            object errObj;
            if (obj.TryGetValue("error", out errObj))
            {
                Dictionary<string, object> err = errObj as Dictionary<string, object>;
                string msg = err != null && err.ContainsKey("message") && err["message"] != null
                    ? err["message"].ToString()
                    : "未知错误";
                throw new ChatException("接口报错：" + msg);
            }

            object choicesObj;
            if (!obj.TryGetValue("choices", out choicesObj)) return null;
            IList choices = choicesObj as IList;
            if (choices == null || choices.Count == 0) return null;
            Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
            if (first == null) return null;

            object deltaObj;
            if (!first.TryGetValue("delta", out deltaObj)) return null;
            Dictionary<string, object> delta = deltaObj as Dictionary<string, object>;
            if (delta == null) return null;

            object content;
            if (!delta.TryGetValue("content", out content) || content == null) return null;
            return content as string;
        }

        private static string Friendly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "（空响应）";
            try
            {
                Dictionary<string, object> obj = Ser.Deserialize<Dictionary<string, object>>(raw);
                if (obj != null)
                {
                    object errObj;
                    if (obj.TryGetValue("error", out errObj))
                    {
                        Dictionary<string, object> err = errObj as Dictionary<string, object>;
                        if (err != null && err.ContainsKey("message") && err["message"] != null)
                            return err["message"].ToString();
                    }
                    object msg;
                    if (obj.TryGetValue("message", out msg) && msg != null) return msg.ToString();
                }
            }
            catch { }
            return raw.Length > 300 ? raw.Substring(0, 300) : raw;
        }
    }
}
