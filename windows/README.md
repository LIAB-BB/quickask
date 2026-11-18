# 随手问 · Windows 版

Windows 上的随手问：按 `Ctrl+Alt+Space` 呼出一个无边框小窗，输入问题，回答流式吐出来；
Markdown 渲染、每次问答落一个 Markdown 文件。用 WinForms + WebView2 写成，不是 Electron，
也不需要安装 .NET SDK。

## 和 macOS 版的关系

- **产品概念一致**：一个热键、一个输入框、流式回答、模型胶囊、日志、设置。
- **代码独立**：macOS 版是 Swift / AppKit；这里是 C# + WinForms，界面是一张 HTML/CSS/JS
  页面交给 WebView2 渲染。没有可以直接复用的 Swift 代码，UI 也是重新写的。
- **Windows 独有的部分**：Win32 全局热键、托盘菜单、窗口定位/吸附、WebView2 宿主、
  联网搜索（Bing RSS / DeepSeek Search API）。

## 系统要求

- Windows 10/11 x64
- .NET Framework 4.8（系统自带）
- WebView2 Runtime（Windows 11 自带；Windows 10 装 Evergreen Runtime）
- PowerShell 5.1+；构建用系统自带的 `csc.exe`，不需要 .NET SDK

## 构建

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File tools\fetch-webview2.ps1
powershell -ExecutionPolicy Bypass -File build.ps1
```

`fetch-webview2.ps1` 只跑一次，把 NuGet 上的 WebView2 SDK 解到 `vendor\webview2\`；
`build.ps1` 产出 `dist\QuickAsk.exe` 和 `QuickAsk.Selftest.exe`，并把 3 个 DLL 复制到
`dist\`。运行的时候这 4 个文件要放在一起。

## 用它

| 操作 | 效果 |
| --- | --- |
| `Ctrl+Alt+Space` | 呼出 / 收起小窗 |
| `Enter` | 发送；回答流式输出 |
| `Shift+Enter` | 换行 |
| `Esc` | 关闭；正在生成时先打断 |
| `Ctrl+N` | 新对话 |
| 拖窗口顶部 | 移动 |
| 面板上右键 | 打开设置 / 配置文件 / 新对话 / 退出（托盘图标被系统藏起来时的兜底） |
| 托盘图标 | 打开、新对话、设置、开机自启、退出 |

第一次启动会弹设置：点 **从 DSH 导入 Key**（读 `%USERPROFILE%\.dsh\.credentials.yaml` 的
`DEEPSEEK_API_KEY`），或者手动填。默认模型 `deepseek-flash`、接口 `https://api.deepseek.com`。

Windows 11 默认把新托盘图标收进 `^` 溢出区；如果看不到，点任务栏右下角的 `^`，或者
设置 → 个性化 → 任务栏 → 其他系统托盘图标里打开。

## 配置

默认写在 `%APPDATA%\QuickAskWin\config.json`。如果 exe 旁边存在 `data\config.json`，
会优先用这个便携目录；也可以用 `QUICKASK_HOME` 指定。

完整字段见 [`config.example.json`](config.example.json)，常用项：

```jsonc
{
  "BaseURL": "https://api.deepseek.com",
  "ApiKey": "",
  "Model": "deepseek-flash",
  "Hotkey": "Control+Alt+Space",
  "LogEnabled": true,
  "LogDir": "%USERPROFILE%\\Documents\\QuickAsk",
  "SearchEnabled": false,
  "SearchProvider": "bing",              // "bing" 或 "deepseek"
  "SearchBaseURL": "https://api.deepseek.com/v1/tools",
  "SearchMinScore": 1.0,
  "ScreenMode": "primary"                // "primary" 或 "cursor"
}
```

环境变量可以临时覆盖：`QUICKASK_API_KEY`、`QUICKASK_BASE_URL`、`QUICKASK_MODEL`、
`QUICKASK_HOTKEY`、`QUICKASK_SEARCH`、`QUICKASK_SEARCH_KEY`、`QUICKASK_SEARCH_PROVIDER`、
`QUICKASK_HOME`。

## 联网搜索

点右上角「联网」开关（会记住）。开启后每次提问：先用模型把问题改写成检索词 → 搜索 →
把前 5 条摘要交给模型 → 回答带 `[1][2]` 引用和来源列表。

| 搜索源 | 说明 |
| --- | --- |
| Bing（默认） | 抓 Bing 的 RSS 结果页，免费、无需 Key；结果质量一般，条款限个人非商业用途 |
| DeepSeek Search API | 官方检索接口，**复用对话的 API Key**；5 元 / 1,000 次成功请求，账号级 100 QPS |

DeepSeek 结果按 `rerank_score` 排序，低于 `SearchMinScore` 的丢弃；搜不到或全部低分时
不注入资料，直接回答，避免劣质结果污染答案。搜索失败、超时、402/429 都只提示原因，
不影响正常回答。

## 自检

`--selftest` / `--searchtest` 需要一份配置指向本地 mock（或真实接口），配置最少包含
`BaseURL`、`ApiKey`、`Model` 三项：

```powershell
# 终端 1：本地 SSE mock，监听 18080
node tools\mock-server.mjs

# 终端 2：配置里 BaseURL 填 http://127.0.0.1:18080，ApiKey 随意填
dist\QuickAsk.Selftest.exe --selftest --config C:\path\to\mock.json
```

`--uitest` 会真的打开窗口、自动输入并等待回答结束，适合端到端冒烟：

```powershell
dist\QuickAsk.exe --uitest --config C:\path\to\mock.json
```

搜索结果解析可以用 `tools\mock-search.mjs`（监听 18081，请求 `/v1/tools/search`）验证。

## 已知限制

- 没有图片 / 截图输入（macOS 版有）。
- 只用 .NET Framework 4.8 和 C# 5 语法，构建依赖系统自带的 `csc.exe`。
- 托盘图标在某些 Windows 11 环境会被收进 `^` 或干脆被 shell 丢掉；面板右键菜单和热键
  可以覆盖全部操作。
- 搜索只拿摘要，`need_content` 为 false，不抓正文。
- 窗口默认出现在主屏；多显示器想跟随鼠标就把 `ScreenMode` 改成 `"cursor"`。

## 目录

```
windows/
  src/                 C# 源码（Program / PanelForm / ChatEngine / WebSearch / Config / ...）
  ui.html              界面（HTML/CSS/JS，WebView2 渲染）
  build.ps1            csc 构建脚本
  app.manifest         DPI 感知 + asInvoker
  config.example.json  配置示例
  assets/app.ico       应用图标（由 tools/make-icon.ps1 生成）
  tools/
    fetch-webview2.ps1 下载 WebView2 SDK
    mock-server.mjs    本地 SSE mock
    mock-search.mjs    本地搜索 mock
    make-icon.ps1      生成图标
    hotkey-probe.cs    热键占用探测
```
