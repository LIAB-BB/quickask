using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QuickAskWin
{
    /// <summary>The floating panel: a borderless WebView2 window summoned by a global hotkey.</summary>
    public class PanelForm : Form
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0x5141;
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private readonly AppConfig config;
        private readonly string configPath;
        private readonly WebView2 web;
        private readonly JavaScriptSerializer ser = new JavaScriptSerializer();
        private readonly List<Dictionary<string, object>> history = new List<Dictionary<string, object>>();
        private readonly object deltaLock = new object();
        private readonly StringBuilder pendingDelta = new StringBuilder();
        private readonly StringBuilder fullAnswer = new StringBuilder();
        private readonly System.Windows.Forms.Timer flushTimer;

        public bool UiTest { get; set; }

        private NotifyIcon tray;
        private ContextMenuStrip panelMenu;
        private bool webReady;
        private bool expanded;
        private bool streaming;
        private bool exiting;
        private bool hotkeyRegistered;
        private CancellationTokenSource cts;
        private DateTime started;
        private string lastQuestion = "";

        public PanelForm(AppConfig cfg, string path)
        {
            config = cfg;
            configPath = path;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(247, 247, 248);
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "随手问";
            AutoScaleMode = AutoScaleMode.None;

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(247, 247, 248);
            Controls.Add(web);

            flushTimer = new System.Windows.Forms.Timer();
            flushTimer.Interval = 60;
            flushTimer.Tick += delegate { FlushDelta(); };

            BuildTray();
            BuildPanelMenu();
            CreateHandle();
            RegisterHotkey();
            PositionPanel();
            InitWeb();
        }

        private int S(int px)
        {
            return (int)Math.Round(px * (DeviceDpi / 96.0));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting)
            {
                e.Cancel = true;
                HidePanel();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                Toggle();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (config.HideOnBlur && !streaming && Visible) HidePanel();
        }

        // ── hotkey ────────────────────────────────────────────────────────────

        private static bool ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            foreach (string raw in parts)
            {
                string p = raw.Trim().ToLowerInvariant();
                if (p == "control" || p == "ctrl") mods |= 0x0002;
                else if (p == "alt") mods |= 0x0001;
                else if (p == "shift") mods |= 0x0004;
                else if (p == "win" || p == "windows") mods |= 0x0008;
                else if (p == "space") vk = 0x20;
                else if (p == "enter") vk = 0x0D;
                else if (p == "escape") vk = 0x1B;
                else if (p == "tab") vk = 0x09;
                else if (p == "backquote") vk = 0xC0;
                else if (p.Length == 1 && p[0] >= 'a' && p[0] <= 'z') vk = (uint)char.ToUpperInvariant(p[0]);
                else if (p.Length >= 2 && p[0] == 'f')
                {
                    int n;
                    if (int.TryParse(p.Substring(1), out n) && n >= 1 && n <= 24) vk = (uint)(0x70 + n - 1);
                }
                else return false;
            }
            return vk != 0 && mods != 0;
        }

        private void RegisterHotkey()
        {
            if (hotkeyRegistered && Handle != IntPtr.Zero)
            {
                UnregisterHotKey(Handle, HotkeyId);
                hotkeyRegistered = false;
            }
            if (Handle == IntPtr.Zero) return;
            uint mods, vk;
            if (!ParseHotkey(config.Hotkey, out mods, out vk))
            {
                config.Hotkey = "Control+Alt+Space";
                ParseHotkey(config.Hotkey, out mods, out vk);
            }
            hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, mods, vk);
            if (!hotkeyRegistered && tray != null)
            {
                tray.BalloonTipTitle = "随手问";
                tray.BalloonTipText = "全局热键 " + config.Hotkey + " 被占用，点托盘图标仍可打开。";
                tray.ShowBalloonTip(4000);
            }
        }

        public void Toggle()
        {
            if (Visible) HidePanel();
            else ShowPanel();
        }

        public void ShowPanel()
        {
            if (string.IsNullOrEmpty(config.ApiKey))
            {
                // First run: let the user fill the key before asking anything.
                OpenSettings();
                if (string.IsNullOrEmpty(config.ApiKey)) return;
            }
            PositionPanel();
            Show();
            Activate();
            TopMost = true;
            if (webReady)
            {
                web.Focus();
                Post(Message("focus"));
            }
        }

        public void HidePanel()
        {
            Hide();
        }

        // ── geometry ──────────────────────────────────────────────────────────

        private void PositionPanel()
        {
            Screen screen;
            if (string.Equals(config.ScreenMode, "cursor", StringComparison.OrdinalIgnoreCase))
                screen = Screen.FromPoint(Cursor.Position);
            else
                screen = Screen.PrimaryScreen;

            Rectangle wa = screen.WorkingArea;
            int width = S(720);
            int height = expanded
                ? Math.Min(S(660), (int)(wa.Height * 0.62))
                : S(104);
            int x = wa.Left + (wa.Width - width) / 2;
            int y = wa.Top + (int)(wa.Height * 0.18);
            x = Math.Max(wa.Left + S(8), Math.Min(x, wa.Right - width - S(8)));
            y = Math.Max(wa.Top + S(8), Math.Min(y, wa.Bottom - height - S(8)));
            SetBounds(x, y, width, height);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), S(18)))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void SetExpanded(bool on)
        {
            if (expanded == on) return;
            expanded = on;
            PositionPanel();
        }

        // ── tray ──────────────────────────────────────────────────────────────

        private void BuildTray()
        {
            Diag("BuildTray enter");
            try
            {
                tray = new NotifyIcon();
                Icon icon = null;
                try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch (Exception ex) { Diag("icon extract failed: " + ex.Message); }
                tray.Icon = icon ?? SystemIcons.Application;
                tray.Text = "随手问";
                tray.Visible = true;
                Diag("tray.Visible=" + tray.Visible + " iconNull=" + (tray.Icon == null));
                tray.DoubleClick += delegate { ShowPanel(); };

                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("打开随手问", null, delegate { ShowPanel(); });
                menu.Items.Add("新对话", null, delegate { NewChat(); ShowPanel(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("设置…", null, delegate { OpenSettings(); });
                menu.Items.Add("打开配置文件", null, delegate { OpenConfigFile(); });
                ToolStripMenuItem autoStart = new ToolStripMenuItem("开机自动启动");
                autoStart.CheckOnClick = true;
                autoStart.Checked = AutoStartEnabled();
                autoStart.Click += delegate(object s, EventArgs e)
                {
                    SetAutoStart(((ToolStripMenuItem)s).Checked);
                };
                menu.Items.Add(autoStart);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, delegate { ExitApp(); });
                tray.ContextMenuStrip = menu;
            }
            catch (Exception ex)
            {
                Diag("BuildTray failed: " + ex);
                throw;
            }
        }

        private static void Diag(string text)
        {
            try
            {
                string dir = ConfigStore.DataDir();
                File.AppendAllText(Path.Combine(dir, "diagnostics.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + "\r\n");
            }
            catch { }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "QuickAsk";

        /// <summary>Fallback menu for environments where the tray icon is hidden.</summary>
        private void BuildPanelMenu()
        {
            panelMenu = new ContextMenuStrip();
            panelMenu.Items.Add("打开设置…", null, delegate { OpenSettings(); });
            panelMenu.Items.Add("打开配置文件", null, delegate { OpenConfigFile(); });
            panelMenu.Items.Add(new ToolStripSeparator());
            panelMenu.Items.Add("新对话", null, delegate { NewChat(); ShowPanel(); });
            panelMenu.Items.Add("退出", null, delegate { ExitApp(); });
        }

        private void ShowPanelMenu()
        {
            if (panelMenu == null) return;
            panelMenu.Show(Cursor.Position);
        }

        private static bool AutoStartEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        private void SetAutoStart(bool enabled)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --silent");
                    else key.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机启动失败：" + ex.Message, "随手问",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── web ───────────────────────────────────────────────────────────────

        private async void InitWeb()
        {
            try
            {
                string dataDir = null;
                string[] candidates = new string[]
                {
                    Path.Combine(ConfigStore.DataDir(), "WebView2"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "WebView2"),
                    Path.Combine(Path.GetTempPath(), "QuickAskWin-WebView2")
                };
                foreach (string candidate in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        string probe = Path.Combine(candidate, ".write-test");
                        File.WriteAllText(probe, "1");
                        File.Delete(probe);
                        dataDir = candidate;
                        break;
                    }
                    catch { }
                }
                if (dataDir == null) throw new InvalidOperationException("没有可写的 WebView2 数据目录");                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await web.EnsureCoreWebView2Async(env);
                CoreWebView2 core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.WebMessageReceived += OnWebMessage;
                core.NavigateToString(LoadHtml());
                webReady = true;
                if (UiTest) RunUiTest();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "WebView2 初始化失败：" + ex.Message + "\n\n请确认已安装 Microsoft Edge WebView2 运行时。",
                    "随手问", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string LoadHtml()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream("ui.html"))
            {
                if (stream == null) return "<html><body>ui.html missing</body></html>";
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>Headless-ish UI check: type a prompt, click send, log the answer.</summary>
        private async void RunUiTest()
        {
            await Task.Delay(1800);
            string log = Path.Combine(ConfigStore.DataDir(), "uitest.log");
            try
            {
                string sent = await web.CoreWebView2.ExecuteScriptAsync(
                    "(function(){var i=document.getElementById('input');" +
                    "if(!i)return 'no-input';i.value='DeepSeek V4.1 Flash 是什么时候发布的？';" +
                    "document.getElementById('send').click();return 'sent';})()");
                File.AppendAllText(log, "send=" + sent + Environment.NewLine);

                for (int i = 0; i < 90; i++)
                {
                    await Task.Delay(500);
                    string finished = await web.CoreWebView2.ExecuteScriptAsync(
                        "(document.querySelector('.meta')||document.querySelector('.msg.error'))?'1':'0'");
                    if (finished != "\"1\"") continue;

                    string text = await web.CoreWebView2.ExecuteScriptAsync(
                        "(document.querySelector('.msg.assistant')||{}).innerText||''");
                    text = Unquote(text);
                    if (text.Length > 0) File.AppendAllText(log, "answer=" + text + Environment.NewLine);
                    string src = await web.CoreWebView2.ExecuteScriptAsync(
                        "(document.querySelector('.sources')||{}).innerText||''");
                    src = Unquote(src);
                    if (src.Length > 0) File.AppendAllText(log, "sources=" + src + Environment.NewLine);
                    string error = await web.CoreWebView2.ExecuteScriptAsync(
                        "(document.querySelector('.msg.error')||{}).innerText||''");
                    error = Unquote(error);
                    if (error.Length > 0) File.AppendAllText(log, "error=" + error + Environment.NewLine);
                    break;
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(log, "error=" + ex + Environment.NewLine); }
                catch { }
            }
            ExitApp();
        }

        private static string Unquote(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return "";
            if (json.Length >= 2 && json[0] == '"' && json[json.Length - 1] == '"')
            {
                string inner = json.Substring(1, json.Length - 2);
                return inner.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return json;
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json;
            try { json = e.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrEmpty(json)) return;

            Dictionary<string, object> msg;
            try { msg = ser.Deserialize<Dictionary<string, object>>(json); }
            catch { return; }
            if (msg == null || !msg.ContainsKey("t")) return;

            string t = Convert.ToString(msg["t"]);
            switch (t)
            {
                case "ready": PostState(); break;
                case "send": Send(Convert.ToString(msg.ContainsKey("text") ? msg["text"] : "")); break;
                case "cancel": CancelStream(); break;
                case "escape":
                    if (streaming) CancelStream();
                    HidePanel();
                    break;
                case "hide": HidePanel(); break;
                case "drag": StartDrag(); break;
                case "open":
                    try
                    {
                        string url = Convert.ToString(msg.ContainsKey("url") ? msg["url"] : "");
                        if (url.StartsWith("http://") || url.StartsWith("https://"))
                            Process.Start(url);
                    }
                    catch { }
                    break;
                case "settings": OpenSettings(); break;
                case "open-config": OpenConfigFile(); break;
                case "new": NewChat(); break;
                case "effort": ToggleEffort(); break;
                case "search": ToggleSearch(); break;
                case "menu": ShowPanelMenu(); break;
                case "model": ApplyPreset(Convert.ToString(msg.ContainsKey("name") ? msg["name"] : "")); break;
            }
        }

        private void Post(Dictionary<string, object> message)
        {
            if (!webReady || IsDisposed) return;
            try { web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(message)); }
            catch { }
        }

        private static Dictionary<string, object> Message(string type)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["t"] = type;
            return m;
        }

        private void PostState()
        {
            Dictionary<string, object> m = Message("state");
            m["model"] = ModelDisplayName();
            List<string> names = new List<string>();
            foreach (ModelPreset p in config.AllPresets()) names.Add(p.Name);
            m["presets"] = names;
            m["deepThink"] = config.DeepThink;
            m["supportsEffort"] = config.SupportsEffort;
            m["streaming"] = streaming;
            m["hotkey"] = config.Hotkey;
            m["search"] = config.SearchEnabled;
            m["searchProvider"] = WebSearch.IsDeepSeek(config) ? "DeepSeek" : "Bing";
            Post(m);
        }

        private string ModelDisplayName()
        {
            foreach (ModelPreset p in config.AllPresets())
            {
                if (p.BaseURL == config.BaseURL && p.Model == config.Model && !string.IsNullOrEmpty(p.Name))
                    return p.Name;
            }
            return config.Model;
        }

        private void StartDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        private void FlushDelta()
        {
            string chunk;
            lock (deltaLock)
            {
                if (pendingDelta.Length == 0) return;
                chunk = pendingDelta.ToString();
                pendingDelta.Length = 0;
            }
            Dictionary<string, object> m = Message("delta");
            m["text"] = chunk;
            Post(m);
        }

        // ── conversation ──────────────────────────────────────────────────────

        private void NewChat()
        {
            if (streaming) CancelStream();
            history.Clear();
            expanded = false;
            PositionPanel();
            Post(Message("clear"));
            PostState();
        }

        private void ToggleEffort()
        {
            if (!config.SupportsEffort) return;
            config.DeepThink = !config.DeepThink;
            ConfigStore.Save(config, configPath);
            PostState();
        }

        private void ToggleSearch()
        {
            config.SearchEnabled = !config.SearchEnabled;
            ConfigStore.Save(config, configPath);
            PostState();
        }

        private void ApplyPreset(string name)
        {
            foreach (ModelPreset p in config.AllPresets())
            {
                if (p.Name != name) continue;
                config.BaseURL = p.BaseURL;
                config.Model = p.Model;
                if (!string.IsNullOrEmpty(p.ApiKey)) config.ApiKey = p.ApiKey;
                config.ExtraHeaders = p.Headers != null
                    ? new Dictionary<string, string>(p.Headers)
                    : new Dictionary<string, string>();
                config.SupportsEffort = p.SupportsEffort;
                ConfigStore.Save(config, configPath);
                PostState();
                return;
            }
        }

        private void EnsureSystemPrompt()
        {
            if (history.Count > 0) return;
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["role"] = "system";
            msg["content"] = config.SystemPrompt;
            history.Add(msg);
        }

        private void Send(string text)
        {
            if (streaming) return;
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (string.IsNullOrEmpty(config.ApiKey))
            {
                OpenSettings();
                return;
            }

            if (!expanded)
            {
                expanded = true;
                PositionPanel();
            }

            lastQuestion = text;
            started = DateTime.Now;
            streaming = true;
            cts = new CancellationTokenSource();
            EnsureSystemPrompt();

            Dictionary<string, object> user = new Dictionary<string, object>();
            user["role"] = "user";
            user["content"] = text;
            history.Add(user);

            lock (deltaLock) { pendingDelta.Length = 0; fullAnswer.Length = 0; }
            Post(Message("start"));
            flushTimer.Start();

            Task task = RunStream(cts.Token);
        }

        private async Task RunStream(CancellationToken ct)
        {
            List<Dictionary<string, object>> request = history;
            List<SearchItem> sources = null;
            try
            {
                if (config.SearchEnabled)
                {
                    PostStatus("正在联网…");
                    string query = lastQuestion;
                    try
                    {
                        query = await BuildSearchQuery(lastQuestion, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        PostStatus("查询改写失败，直接用原问题搜索（" + ex.Message + "）");
                    }

                    try
                    {
                        sources = await WebSearch.SearchAsync(query, lastQuestion, config, ct);
                        if (sources != null && sources.Count > 0)
                        {
                            PostSources(sources);
                            PostStatus("正在整理资料…");
                            request = new List<Dictionary<string, object>>(history);
                            request.Add(SearchContext(sources));
                        }
                        else
                        {
                            PostStatus("没有找到相关结果，直接回答…");
                            sources = null;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        PostStatus("联网失败：" + ex.Message);
                        sources = null;
                    }
                }

                await ChatEngine.StreamAsync(config, request, delegate(string delta)
                {
                    lock (deltaLock)
                    {
                        pendingDelta.Append(delta);
                        fullAnswer.Append(delta);
                    }
                }, ct);
                FlushDelta();

                string answer;
                lock (deltaLock) { answer = fullAnswer.ToString(); }
                if (answer.Length > 0)
                {
                    Dictionary<string, object> assistant = new Dictionary<string, object>();
                    assistant["role"] = "assistant";
                    assistant["content"] = answer;
                    history.Add(assistant);
                }

                double seconds = (DateTime.Now - started).TotalSeconds;
                Dictionary<string, object> done = Message("done");
                done["seconds"] = seconds;
                Post(done);
                try
                {
                    ChatLog.Write(config, lastQuestion, answer, ModelDisplayName(), seconds, sources);
                }
                catch (Exception logError)
                {
                    // A logging failure must never turn a finished answer into an error.
                    Console.Error.WriteLine("[quick-ask] chat log failed: " + logError.Message);
                }
            }
            catch (OperationCanceledException)
            {
                Post(Message("cancelled"));
            }
            catch (ObjectDisposedException)
            {
                Post(Message("cancelled"));
            }
            catch (Exception ex)
            {
                FlushDelta();
                Dictionary<string, object> err = Message("error");
                err["text"] = ex.Message;
                Post(err);
            }
            finally
            {
                flushTimer.Stop();
                streaming = false;
                if (cts != null) { cts.Dispose(); cts = null; }
                PostState();
            }
        }

        /// <summary>Rewrite the question into a short search query; falls back to the question.</summary>
        private async Task<string> BuildSearchQuery(string question, CancellationToken ct)
        {
            AppConfig helper = config.Clone();
            helper.SupportsEffort = false;
            helper.MaxTokens = 64;

            List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
            Dictionary<string, object> system = new Dictionary<string, object>();
            system["role"] = "system";
            system["content"] =
                "你是搜索查询改写器。今天是 " + DateTime.Now.ToString("yyyy-MM-dd") + "。" +
                "把用户的问题改写成一条适合搜索引擎的中文查询：" +
                "保留关键实体、地名、时间和数字，去掉口语、客套和代词；" +
                "如果用户明确限制站点、语言或时间，可使用 site:、lang:、after:、before: 语法；" +
                "只输出查询本身，不要引号、不要解释。";
            messages.Add(system);
            Dictionary<string, object> user = new Dictionary<string, object>();
            user["role"] = "user";
            user["content"] = question;
            messages.Add(user);

            string raw = await ChatEngine.CompleteAsync(helper, messages, ct);
            string q = (raw ?? "").Trim();
            q = q.Trim('"', '\'', '“', '”', '‘', '’');
            q = q.Replace("\r", " ").Replace("\n", " ");
            while (q.Contains("  ")) q = q.Replace("  ", " ");
            if (q.Length > 120) q = q.Substring(0, 120);
            return q.Length >= 2 ? q : question;
        }

        private static Dictionary<string, object> SearchContext(List<SearchItem> items)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("以下是搜索引擎返回的资料（可能不完整、过时，或与问题无关）：");
            sb.AppendLine();
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append('[').Append(i + 1).Append("] ").Append(items[i].Title);
                if (items[i].Date.Length > 0) sb.Append("（").Append(items[i].Date).Append("）");
                sb.AppendLine();
                sb.AppendLine(items[i].Url);
                if (items[i].Snippet.Length > 0) sb.AppendLine(items[i].Snippet);
                sb.AppendLine();
            }
            sb.AppendLine("使用规则：");
            sb.AppendLine("1. 只有当资料确实支持某个说法时才引用，并在句末用 [1][2] 标注；不要为了引用而引用。");
            sb.AppendLine("2. 资料没有覆盖的部分，先说明“资料未提及”，再凭你自己的知识回答，并区分哪些来自资料、哪些是模型知识。");
            sb.AppendLine("3. 如果资料整体与问题无关，直接忽略它们，不要硬凑引用；可以说明“未检索到可靠资料”。");
            sb.AppendLine("4. 不要编造链接、日期、作者或来源；不确定就说不确定。");
            sb.AppendLine("5. 答案要短而准：先给结论，再给必要细节。");

            Dictionary<string, object> message = new Dictionary<string, object>();
            message["role"] = "user";
            message["content"] = sb.ToString();
            return message;
        }

        private void PostStatus(string text)
        {
            Dictionary<string, object> m = Message("status");
            m["text"] = text;
            Post(m);
        }

        private void PostSources(List<SearchItem> items)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (SearchItem item in items)
            {
                Dictionary<string, object> row = new Dictionary<string, object>();
                row["title"] = item.Title;
                row["url"] = item.Url;
                list.Add(row);
            }
            Dictionary<string, object> m = Message("sources");
            m["items"] = list;
            Post(m);
        }

        private void CancelStream()
        {
            if (!streaming || cts == null) return;
            try { cts.Cancel(); }
            catch { }
        }

        // ── settings / files ──────────────────────────────────────────────────

        private void OpenSettings()
        {
            using (SettingsForm form = new SettingsForm(config.Clone(), configPath))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                AppConfig edited = form.Result;
                config.BaseURL = edited.BaseURL;
                config.ApiKey = edited.ApiKey;
                config.Model = edited.Model;
                config.Hotkey = edited.Hotkey;
                config.LogDir = edited.LogDir;
                config.LogEnabled = edited.LogEnabled;
                config.DeepThink = edited.DeepThink;
                config.SupportsEffort = edited.SupportsEffort;
                config.MaxTokens = edited.MaxTokens;
                config.HideOnBlur = edited.HideOnBlur;
                config.SearchEnabled = edited.SearchEnabled;
                config.SearchProvider = edited.SearchProvider;
                config.SearchApiKey = edited.SearchApiKey;
                ConfigStore.Save(config, configPath);
                RegisterHotkey();
                PostState();
            }
        }

        private void OpenConfigFile()
        {
            try
            {
                if (!File.Exists(configPath)) ConfigStore.Save(config, configPath);
                Process.Start("explorer.exe", "/select,\"" + configPath + "\"");
            }
            catch { }
        }

        public void ExitApp()
        {
            exiting = true;
            if (streaming) CancelStream();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
            Close();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (flushTimer != null) flushTimer.Dispose();
                if (tray != null) tray.Dispose();
                if (web != null) web.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
