using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace QuickAskWin
{
    /// <summary>One model the capsule can switch to.</summary>
    public class ModelPreset
    {
        public string Name { get; set; }
        public string BaseURL { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public bool SupportsEffort { get; set; }

        public ModelPreset()
        {
            Name = "";
            BaseURL = "";
            Model = "";
            ApiKey = "";
            Headers = new Dictionary<string, string>();
            SupportsEffort = false;
        }
    }

    /// <summary>Everything persisted in %APPDATA%\QuickAskWin\config.json.</summary>
    public class AppConfig
    {
        public string BaseURL { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public bool DeepThink { get; set; }
        public bool SupportsEffort { get; set; }
        public Dictionary<string, string> ExtraHeaders { get; set; }
        public List<ModelPreset> Presets { get; set; }
        public bool LogEnabled { get; set; }
        public string LogDir { get; set; }
        public string SystemPrompt { get; set; }
        public string Hotkey { get; set; }
        public int MaxTokens { get; set; }
        public bool HideOnBlur { get; set; }
        public bool SearchEnabled { get; set; }
        public string SearchProvider { get; set; }
        public string SearchApiKey { get; set; }
        public string SearchBaseURL { get; set; }
        public int SearchMaxResults { get; set; }
        public int SearchTimeoutMs { get; set; }
        public double SearchMinScore { get; set; }
        public string ScreenMode { get; set; }

        public AppConfig()
        {
            BaseURL = "https://api.deepseek.com";
            ApiKey = "";
            Model = "deepseek-flash";
            DeepThink = false;
            SupportsEffort = true;
            ExtraHeaders = new Dictionary<string, string>();
            Presets = new List<ModelPreset>();
            LogEnabled = true;
            LogDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuickAsk");
            SystemPrompt =
                "你是用户 Windows 上的随身答疑助手「随手问」。\n" +
                "默认用简体中文回答，除非用户使用其他语言提问。\n" +
                "回答要短而准：先给结论，再给必要的细节或步骤；不要写引言和客套话。\n" +
                "用户可能追问，请结合上下文回答。需要分点时用简短列表，代码用代码块。";
            Hotkey = "Control+Alt+Space";
            MaxTokens = 4096;
            HideOnBlur = false;
            SearchEnabled = false;
            SearchProvider = "bing";
            SearchApiKey = "";
            SearchBaseURL = "https://api.deepseek.com/v1/tools";
            SearchMaxResults = 5;
            SearchTimeoutMs = 10000;
            SearchMinScore = 1.0;
            ScreenMode = "primary";
        }

        /// <summary>The capsule list: explicit presets, or the top-level values.</summary>
        public List<ModelPreset> AllPresets()
        {
            if (Presets != null && Presets.Count > 0) return Presets;
            List<ModelPreset> list = new List<ModelPreset>();
            list.Add(new ModelPreset
            {
                Name = Model + " · 直连",
                BaseURL = BaseURL,
                Model = Model,
                ApiKey = ApiKey,
                Headers = new Dictionary<string, string>(ExtraHeaders),
                SupportsEffort = SupportsEffort
            });
            return list;
        }

        public string Effort()
        {
            if (!SupportsEffort) return null;
            return DeepThink ? "high" : "low";
        }

        public AppConfig Clone()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            return ser.Deserialize<AppConfig>(ser.Serialize(this));
        }
    }

    public static class ConfigStore
    {
        /// <summary>App data root, falling back when the roaming profile is not writable.</summary>
        public static string DataDir()
        {
            string home = Environment.GetEnvironmentVariable("QUICKASK_HOME");
            if (!string.IsNullOrEmpty(home))
            {
                try
                {
                    Directory.CreateDirectory(home);
                    return home;
                }
                catch { }
            }

            // Once a portable data folder exists, keep using it across launches
            // (a sandboxed launch may have created it before AppData was writable).
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (File.Exists(Path.Combine(portable, "config.json"))) return portable;

            string[] roots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath()
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    string dir = Path.Combine(root, "QuickAskWin");
                    Directory.CreateDirectory(dir);
                    // Creating the folder is not enough: some tokens can see it but
                    // not write inside it. Prove writability before choosing it.
                    string probe = Path.Combine(dir, ".write-test");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return dir;
                }
                catch { }
            }

            // Launched from a sandbox that only grants the executable's folder:
            // keep portable data beside the exe rather than failing to start.
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                Directory.CreateDirectory(local);
                string probe = Path.Combine(local, ".write-test");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return local;
            }
            catch { }

            return Path.Combine(Path.GetTempPath(), "QuickAskWin");
        }

        /// <summary>Explicit --config path, otherwise the per-user config file.</summary>
        public static string ConfigPath(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath)) return Path.GetFullPath(explicitPath);
            return Path.Combine(DataDir(), "config.json");
        }

        public static AppConfig Load(string path)
        {
            AppConfig cfg = null;
            try
            {
                if (!File.Exists(path)) cfg = new AppConfig();
                else
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    cfg = ser.Deserialize<AppConfig>(File.ReadAllText(path));
                }
            }
            catch
            {
                cfg = null;
            }
            if (cfg == null) cfg = new AppConfig();

            // Environment overrides keep test keys out of the config file.
            string env = Environment.GetEnvironmentVariable("QUICKASK_BASE_URL");
            if (!string.IsNullOrEmpty(env)) cfg.BaseURL = env;
            env = Environment.GetEnvironmentVariable("QUICKASK_API_KEY");
            if (!string.IsNullOrEmpty(env)) cfg.ApiKey = env;
            env = Environment.GetEnvironmentVariable("QUICKASK_MODEL");
            if (!string.IsNullOrEmpty(env)) cfg.Model = env;
            env = Environment.GetEnvironmentVariable("QUICKASK_HOTKEY");
            if (!string.IsNullOrEmpty(env)) cfg.Hotkey = env;
            env = Environment.GetEnvironmentVariable("QUICKASK_SEARCH");
            if (!string.IsNullOrEmpty(env)) cfg.SearchEnabled = env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase);
            env = Environment.GetEnvironmentVariable("QUICKASK_SEARCH_KEY");
            if (!string.IsNullOrEmpty(env)) cfg.SearchApiKey = env;
            env = Environment.GetEnvironmentVariable("QUICKASK_SEARCH_PROVIDER");
            if (!string.IsNullOrEmpty(env)) cfg.SearchProvider = env;

            if (string.IsNullOrEmpty(cfg.BaseURL)) cfg.BaseURL = "https://api.deepseek.com";
            if (string.IsNullOrEmpty(cfg.Model)) cfg.Model = "deepseek-flash";
            if (cfg.ExtraHeaders == null) cfg.ExtraHeaders = new Dictionary<string, string>();
            if (cfg.Presets == null) cfg.Presets = new List<ModelPreset>();
            if (string.IsNullOrEmpty(cfg.Hotkey)) cfg.Hotkey = "Control+Alt+Space";
            if (cfg.MaxTokens <= 0) cfg.MaxTokens = 4096;
            if (string.IsNullOrEmpty(cfg.SearchProvider)) cfg.SearchProvider = "bing";
            if (string.IsNullOrEmpty(cfg.SearchBaseURL)) cfg.SearchBaseURL = "https://api.deepseek.com/v1/tools";
            if (cfg.SearchMaxResults <= 0) cfg.SearchMaxResults = 5;
            if (cfg.SearchTimeoutMs <= 0) cfg.SearchTimeoutMs = 10000;
            if (cfg.SearchMinScore <= 0) cfg.SearchMinScore = 1.0;
            if (string.IsNullOrEmpty(cfg.ScreenMode)) cfg.ScreenMode = "primary";
            return cfg;
        }

        public static bool Save(AppConfig cfg, string path)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(cfg);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
