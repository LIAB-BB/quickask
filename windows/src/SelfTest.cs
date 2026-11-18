using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace QuickAskWin
{
    /// <summary>Console-only end-to-end check: `QuickAsk.exe --selftest`.</summary>
    public static class SelfTest
    {
        public static int Run(string configPath)
        {
            AppConfig cfg = ConfigStore.Load(configPath);
            if (string.IsNullOrEmpty(cfg.ApiKey))
            {
                Console.Error.WriteLine("selftest: config has no apiKey at " + configPath);
                return 2;
            }

            List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
            Dictionary<string, object> system = new Dictionary<string, object>();
            system["role"] = "system";
            system["content"] = "你是自检助手，只回答一个词。";
            messages.Add(system);
            Dictionary<string, object> user = new Dictionary<string, object>();
            user["role"] = "user";
            user["content"] = "回复 SELFTEST 即可。";
            messages.Add(user);

            int chunks = 0;
            try
            {
                ChatEngine.StreamAsync(cfg, messages, delegate(string delta)
                {
                    chunks++;
                    Console.Write(delta);
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("selftest failed: " + ex.Message);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("selftest: " + chunks + " chunks");
            return chunks > 0 ? 0 : 3;
        }
        public static int Search(string query, string configPath)
        {
            string q = string.IsNullOrEmpty(query) ? "DeepSeek V4.1 Flash" : query;
            AppConfig cfg = ConfigStore.Load(configPath);
            try
            {
                List<SearchItem> items = WebSearch.SearchAsync(q, q, cfg, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.WriteLine("query: " + q + " -> " + items.Count + " results (provider: "
                    + (WebSearch.IsDeepSeek(cfg) ? "deepseek" : "bing") + ")");
                foreach (SearchItem item in items)
                {
                    Console.WriteLine("- " + item.Title);
                    Console.WriteLine("  " + item.Url);
                    Console.WriteLine("  " + item.Snippet);
                }
                return items.Count > 0 ? 0 : 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("search failed: " + ex);
                return 1;
            }
        }
    }
}
