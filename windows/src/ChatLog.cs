using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuickAskWin
{
    /// <summary>One complete Q&A becomes one Markdown file, grouped by day.</summary>
    public static class ChatLog
    {
        public static string Expand(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return Environment.ExpandEnvironmentVariables(path);
        }

        public static void Write(AppConfig cfg, string question, string answer, string model, double seconds,
            List<SearchItem> sources = null)
        {
            if (!cfg.LogEnabled) return;
            string q = (question ?? "").Trim();
            string a = (answer ?? "").Trim();
            if (a.Length == 0) return;

            DateTime now = DateTime.Now;
            string baseDir = Expand(cfg.LogDir);
            if (string.IsNullOrEmpty(baseDir)) return;
            string dir = Path.Combine(baseDir, now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);

            string firstLine = "问答";
            string[] lines = q.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                if (line.Trim().Length > 0) { firstLine = line.Trim(); break; }
            }
            string title = firstLine.Length > 40 ? firstLine.Substring(0, 40) + "…" : firstLine;
            string slug = Sanitize(firstLine.Length > 14 ? firstLine.Substring(0, 14) : firstLine);
            string stamp = now.ToString("HH-mm");

            string file = Path.Combine(dir, stamp + " " + slug + ".md");
            int index = 2;
            while (File.Exists(file))
            {
                file = Path.Combine(dir, stamp + " " + slug + "-" + index + ".md");
                index++;
            }

            StringBuilder body = new StringBuilder();
            body.Append("# ").Append(title).Append("\n\n");
            body.Append("- 时间：").Append(now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            body.Append("- 模型：").Append(model).Append('\n');
            if (seconds > 0) body.Append("- 耗时：").Append(seconds.ToString("0.0")).Append("s\n");
            body.Append('\n');
            if (q != firstLine || q.Length > title.Length)
                body.Append("**问**：\n\n").Append(q).Append("\n\n");
            body.Append("**答**：\n\n").Append(a).Append('\n');

            if (sources != null && sources.Count > 0)
            {
                body.Append("\n**来源**：\n\n");
                for (int i = 0; i < sources.Count; i++)
                {
                    body.Append(i + 1).Append(". [").Append(sources[i].Title)
                        .Append("](").Append(sources[i].Url).Append(")\n");
                }
            }

            File.WriteAllText(file, body.ToString(), new UTF8Encoding(false));
        }

        private static string Sanitize(string s)
        {
            char[] banned = "/\\:*?\"<>|#%".ToCharArray();
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                bool bad = false;
                foreach (char b in banned) if (c == b) { bad = true; break; }
                sb.Append(bad || c == '\t' ? ' ' : c);
            }
            string outText = sb.ToString();
            while (outText.Contains("  ")) outText = outText.Replace("  ", " ");
            outText = outText.Trim();
            return outText.Length == 0 ? "问答" : outText;
        }
    }
}
