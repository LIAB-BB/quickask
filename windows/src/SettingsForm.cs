using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace QuickAskWin
{
    /// <summary>Plain WinForms settings dialog; presets stay JSON-only.</summary>
    public class SettingsForm : Form
    {
        public AppConfig Result { get; private set; }

        private readonly TextBox baseUrl;
        private readonly TextBox apiKey;
        private readonly TextBox model;
        private readonly TextBox hotkey;
        private readonly TextBox logDir;
        private readonly NumericUpDown maxTokens;
        private readonly CheckBox logEnabled;
        private readonly CheckBox deepThink;
        private readonly CheckBox supportsEffort;
        private readonly CheckBox hideOnBlur;
        private readonly CheckBox searchEnabled;
        private readonly ComboBox searchProvider;
        private readonly TextBox searchApiKey;

        public SettingsForm(AppConfig cfg, string configPath)
        {
            Result = cfg;

            Text = "随手问 · 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 530);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(16, 14, 16, 10);
            grid.ColumnCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(grid);

            baseUrl = AddText(grid, "API 地址", cfg.BaseURL, false);
            apiKey = AddText(grid, "API Key", cfg.ApiKey, true);
            model = AddText(grid, "模型", cfg.Model, false);
            hotkey = AddText(grid, "全局热键", cfg.Hotkey, false);
            logDir = AddText(grid, "记录目录", cfg.LogDir, false);

            Label tokenLabel = new Label();
            tokenLabel.Text = "最大 tokens";
            tokenLabel.TextAlign = ContentAlignment.MiddleLeft;
            tokenLabel.Dock = DockStyle.Fill;
            grid.Controls.Add(tokenLabel, 0, 5);
            maxTokens = new NumericUpDown();
            maxTokens.Minimum = 256;
            maxTokens.Maximum = 128000;
            maxTokens.Increment = 256;
            maxTokens.Value = cfg.MaxTokens < 256 ? 4096 : cfg.MaxTokens;
            maxTokens.Dock = DockStyle.Fill;
            grid.Controls.Add(maxTokens, 1, 5);

            searchProvider = AddCombo(grid, "搜索源",
                new string[] { "Bing（免费）", "DeepSeek Search API" },
                WebSearch.IsDeepSeek(cfg) ? 1 : 0);
            searchApiKey = AddText(grid, "搜索 Key", cfg.SearchApiKey, true);

            logEnabled = AddCheck(grid, "每次完整问答写一个 Markdown 文件");
            logEnabled.Checked = cfg.LogEnabled;
            deepThink = AddCheck(grid, "默认深思（reasoning_effort = high）");
            deepThink.Checked = cfg.DeepThink;
            supportsEffort = AddCheck(grid, "接口支持 reasoning_effort");
            supportsEffort.Checked = cfg.SupportsEffort;
            hideOnBlur = AddCheck(grid, "失去焦点时自动隐藏");
            hideOnBlur.Checked = cfg.HideOnBlur;
            searchEnabled = AddCheck(grid, "默认开启联网搜索（Bing，无需额外 Key）");
            searchEnabled.Checked = cfg.SearchEnabled;

            Label note = new Label();
            note.Text = "多模型胶囊请编辑配置文件；DeepSeek 搜索按量计费，Key 留空时复用主 API Key。";
            note.ForeColor = SystemColors.GrayText;
            note.Dock = DockStyle.Fill;
            note.TextAlign = ContentAlignment.MiddleLeft;
            grid.Controls.Add(note, 0, 10);
            grid.SetColumnSpan(note, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 6, 0, 0);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.Width = 84;
            ok.Click += delegate { Apply(); };
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Width = 84;
            Button openConfig = new Button();
            openConfig.Text = "打开配置文件夹";
            openConfig.Width = 130;
            openConfig.Click += delegate { OpenConfig(configPath); };
            Button import = new Button();
            import.Text = "从 DSH 导入 Key";
            import.Width = 128;
            import.Click += delegate { ImportFromDsh(); };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(import);
            buttons.Controls.Add(openConfig);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 54;
            bottom.Padding = new Padding(16, 0, 16, 10);
            bottom.Controls.Add(buttons);
            Controls.Add(bottom);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private TextBox AddText(TableLayoutPanel grid, string label, string value, bool password)
        {
            Label l = new Label();
            l.Text = label;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            grid.Controls.Add(l, 0, grid.RowCount);
            TextBox box = new TextBox();
            box.Text = value;
            if (password) box.UseSystemPasswordChar = true;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 5, 3, 5);
            grid.Controls.Add(box, 1, grid.RowCount);
            grid.RowCount++;
            return box;
        }

        private ComboBox AddCombo(TableLayoutPanel grid, string label, string[] items, int selected)
        {
            Label l = new Label();
            l.Text = label;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            grid.Controls.Add(l, 0, grid.RowCount);
            ComboBox box = new ComboBox();
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.Items.AddRange(items);
            box.SelectedIndex = selected >= 0 && selected < items.Length ? selected : 0;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 5, 3, 5);
            grid.Controls.Add(box, 1, grid.RowCount);
            grid.RowCount++;
            return box;
        }

        private CheckBox AddCheck(TableLayoutPanel grid, string label)
        {
            CheckBox box = new CheckBox();
            box.Text = label;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 4, 3, 4);
            grid.Controls.Add(box, 0, grid.RowCount);
            grid.SetColumnSpan(box, 2);
            grid.RowCount++;
            return box;
        }

        private void Apply()
        {
            Result.BaseURL = baseUrl.Text.Trim();
            Result.ApiKey = apiKey.Text.Trim();
            Result.Model = model.Text.Trim();
            Result.Hotkey = hotkey.Text.Trim();
            Result.LogDir = logDir.Text.Trim();
            Result.LogEnabled = logEnabled.Checked;
            Result.DeepThink = deepThink.Checked;
            Result.SupportsEffort = supportsEffort.Checked;
            Result.HideOnBlur = hideOnBlur.Checked;
            Result.SearchEnabled = searchEnabled.Checked;
            Result.SearchProvider = searchProvider.SelectedIndex == 1 ? "deepseek" : "bing";
            Result.SearchApiKey = searchApiKey.Text.Trim();
            Result.MaxTokens = (int)maxTokens.Value;
        }

        private void ImportFromDsh()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", ".credentials.yaml");
                if (!File.Exists(path))
                {
                    MessageBox.Show("没找到 DSH 凭证文件：\n" + path, "随手问",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string key = null;
                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines)
                {
                    string text = line.Trim();
                    if (!text.StartsWith("DEEPSEEK_API_KEY:")) continue;
                    key = text.Substring("DEEPSEEK_API_KEY:".Length).Trim().Trim('"', '\'');
                    break;
                }
                if (string.IsNullOrEmpty(key))
                {
                    MessageBox.Show("凭证文件里没有 DEEPSEEK_API_KEY。", "随手问",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                apiKey.Text = key;
                baseUrl.Text = "https://api.deepseek.com";
                model.Text = "deepseek-flash";
                MessageBox.Show("已从本机 DSH 凭证导入 DeepSeek Key（只读本地文件，未外发）。", "随手问",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "随手问",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenConfig(string configPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(configPath)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + configPath + "\"");
                else if (!string.IsNullOrEmpty(dir)) System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }
    }
}
