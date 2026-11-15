import SwiftUI
import AppKit

// MARK: - 全局钩子

enum AppHooks {
    @MainActor static var screenshot: (() -> Void)?
    @MainActor static var openSettings: (() -> Void)?
    @MainActor static var contentHeight: ((CGFloat) -> Void)?
}

// MARK: - 视觉背景

struct VisualEffectView: NSViewRepresentable {
    var material: NSVisualEffectView.Material = .hudWindow
    var blending: NSVisualEffectView.BlendingMode = .behindWindow

    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = material
        v.blendingMode = blending
        v.state = .active
        return v
    }
    func updateNSView(_ v: NSVisualEffectView, context: Context) {
        v.material = material
        v.blendingMode = blending
    }
}

struct PanelBackground: View {
    @Environment(\.colorScheme) private var scheme
    var body: some View {
        if scheme == .dark {
            Color(red: 0.125, green: 0.127, blue: 0.137)
        } else {
            Color(red: 0.975, green: 0.975, blue: 0.982)
        }
    }
}

// MARK: - 主面板

struct HeightsKey: PreferenceKey {
    static var defaultValue: [String: CGFloat] = [:]
    static func reduce(value: inout [String: CGFloat], nextValue: () -> [String: CGFloat]) {
        value.merge(nextValue()) { max($0, $1) }
    }
}

struct ChatPanelView: View {
    @ObservedObject var model: ChatModel
    @ObservedObject var store: AppStore

    @FocusState private var inputFocused: Bool
    @State private var heights: [String: CGFloat] = [:]

    private var cfg: AppConfig { store.config }

    var body: some View {
        VStack(spacing: 0) {
            composer
            if model.hasConversation {
                Rectangle()
                    .fill(Color.primary.opacity(0.08))
                    .frame(height: 1)
                conversation
            }
            footer
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(
            ZStack {
                PanelBackground()
                LinearGradient(
                    colors: [Color.white.opacity(0.30), Color.clear],
                    startPoint: .top, endPoint: .init(x: 0.5, y: 0.35)
                )
            }
            .ignoresSafeArea()
        )
        .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .strokeBorder(Color.white.opacity(0.18), lineWidth: 1)
        )
        .onPreferenceChange(HeightsKey.self) { h in
            heights = h
            let total = h["composer", default: 0] + h["footer", default: 0] + h["content", default: 0]
            if total > 0 { AppHooks.contentHeight?(total) }
        }
        .onChange(of: model.focusTick) { _, _ in
            inputFocused = true
        }
        .onAppear {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { inputFocused = true }
        }
    }

    // MARK: 输入区

    private var composer: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "sparkle")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .padding(.top, 3)

                TextField("问点什么…", text: $model.input, axis: .vertical)
                    .textFieldStyle(.plain)
                    .font(.system(size: 17))
                    .lineLimit(1...7)
                    .focused($inputFocused)
                    .onSubmit { model.ask() }
                    .padding(.vertical, 1)

                if model.isStreaming {
                    Button {
                        model.cancel()
                    } label: {
                        Image(systemName: "stop.circle.fill")
                            .font(.system(size: 22))
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("停止生成 (Esc 先停再关)")
                } else {
                    Button {
                        model.ask()
                    } label: {
                        Image(systemName: "arrow.up.circle.fill")
                            .font(.system(size: 22))
                            .foregroundStyle(canSend ? Color.accentColor : Color.secondary.opacity(0.45))
                    }
                    .buttonStyle(.plain)
                    .disabled(!canSend)
                    .help("发送 (⏎)")
                }
            }

            if !model.attachments.isEmpty {
                HStack(spacing: 8) {
                    ForEach(model.attachments) { att in
                        ZStack(alignment: .topTrailing) {
                            Image(nsImage: att.image)
                                .resizable()
                                .aspectRatio(contentMode: .fit)
                                .frame(width: 54, height: 54)
                                .background(Color.primary.opacity(0.04))
                                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                            Button {
                                model.removeAttachment(att)
                            } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .font(.system(size: 13))
                                    .foregroundStyle(.white, .black.opacity(0.55))
                            }
                            .buttonStyle(.plain)
                            .offset(x: 5, y: -5)
                        }
                    }
                    Spacer()
                }
                .padding(.leading, 27)
            }
        }
        .padding(.horizontal, 18)
        .padding(.top, 16)
        .padding(.bottom, 12)
        .background(
            GeometryReader { g in
                Color.clear.preference(key: HeightsKey.self, value: ["composer": g.size.height])
            }
        )
    }

    private var canSend: Bool {
        !model.input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !model.attachments.isEmpty
    }

    // MARK: 回答区

    private var conversation: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    ForEach(model.messages) { msg in
                        messageView(msg)
                    }

                    if model.isStreaming {
                        if model.streamingText.isEmpty {
                            thinkingView
                        } else {
                            MarkdownText(text: model.streamingText)
                                .textSelection(.enabled)
                        }
                    }

                    if let err = model.errorText {
                        HStack(alignment: .top, spacing: 6) {
                            Image(systemName: "exclamationmark.triangle.fill")
                                .font(.system(size: 12))
                            Text(err).font(.system(size: 13)).textSelection(.enabled)
                        }
                        .foregroundStyle(.red.opacity(0.95))
                        .padding(10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Color.red.opacity(0.08)))
                    }

                    Color.clear.frame(height: 1).id("bottom")
                }
                .padding(.horizontal, 18)
                .padding(.vertical, 16)
                .background(
                    GeometryReader { geo in
                        Color.clear.preference(key: HeightsKey.self, value: ["content": geo.size.height])
                    }
                )
            }
            .onChange(of: model.streamingText) { _, _ in
                withAnimation(.easeOut(duration: 0.12)) { proxy.scrollTo("bottom", anchor: .bottom) }
            }
            .onChange(of: model.messages.count) { _, _ in
                proxy.scrollTo("bottom", anchor: .bottom)
            }
        }
        .frame(maxHeight: .infinity)
    }

    @ViewBuilder
    private func messageView(_ msg: UIMessage) -> some View {
        switch msg.role {
        case .user:
            VStack(alignment: .leading, spacing: 6) {
                Text(msg.text)
                    .font(.system(size: 14.5))
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                if !msg.images.isEmpty {
                    HStack(spacing: 6) {
                        ForEach(msg.images) { img in
                            Image(nsImage: img.image)
                                .resizable()
                                .aspectRatio(contentMode: .fit)
                                .frame(width: 64, height: 46)
                                .background(Color.primary.opacity(0.04))
                                .clipShape(RoundedRectangle(cornerRadius: 6))
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        case .assistant:
            MarkdownText(text: msg.text)
                .textSelection(.enabled)
        }
    }

    private var thinkingView: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            Text(cfg.deepThink ? "深度思考中…" : "思考中…")
                .font(.system(size: 13))
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }

    // MARK: 底栏

    private var footer: some View {
        HStack(spacing: 10) {
            Menu {
                ForEach(store.config.allPresets) { preset in
                    Button {
                        apply(preset)
                    } label: {
                        Text((isActive(preset) ? "✓ " : "") + preset.name)
                    }
                }
            } label: {
                HStack(spacing: 3) {
                    Text(modelLabel)
                        .font(.system(size: 11, weight: .medium))
                    Image(systemName: "chevron.down")
                        .font(.system(size: 7, weight: .bold))
                }
                .foregroundStyle(.secondary)
                .padding(.horizontal, 7)
                .padding(.vertical, 3)
                .background(Capsule().fill(Color.primary.opacity(0.07)))
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("切换模型")

            if cfg.supportsEffort {
            Button {
                var c = store.config
                c.deepThink.toggle()
                store.update(c)
            } label: {
                HStack(spacing: 3) {
                    Image(systemName: cfg.deepThink ? "brain.fill" : "brain")
                        .font(.system(size: 10))
                    Text("深思").font(.system(size: 11))
                }
                .foregroundStyle(cfg.deepThink ? Color.accentColor : Color.secondary)
                .padding(.horizontal, 7)
                .padding(.vertical, 3)
                .background(Capsule().fill(cfg.deepThink ? Color.accentColor.opacity(0.14) : Color.primary.opacity(0.05)))
            }
            .buttonStyle(.plain)
            .help("切换深度思考（更聪明，但更慢）")
            }

            if model.isStreaming {
                Text(String(format: "%.1fs", model.elapsed))
                    .font(.system(size: 11).monospacedDigit())
                    .foregroundStyle(.secondary)
            }

            Spacer()

            if model.hasConversation {
                iconButton("doc.on.doc", "复制回答") { model.copyLast() }
                iconButton("plus.bubble", "新对话 (⌘N)") { model.newConversation() }
            }
            iconButton("photo.badge.plus", "粘贴/截屏提问") { pasteOrShot() }
            iconButton("gearshape", "设置") { AppHooks.openSettings?() }

            Text("esc 关闭")
                .font(.system(size: 10.5))
                .foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 9)
        .background(
            GeometryReader { g in
                Color.clear.preference(key: HeightsKey.self, value: ["footer": g.size.height])
            }
        )
    }

    private var modelLabel: String {
        if let p = cfg.allPresets.first(where: isActive) {
            return String(p.name.split(separator: "·").first ?? "").trimmingCharacters(in: .whitespaces)
        }
        switch cfg.model {
        case "deepseek-flash": return "DeepSeek V4.1 Flash"
        case "deepseek-v4-pro": return "DeepSeek V4 Pro"
        default: return cfg.model
        }
    }

    private func isActive(_ p: ModelPreset) -> Bool {
        func norm(_ s: String) -> String {
            var t = s.trimmingCharacters(in: .whitespaces)
            while t.hasSuffix("/") { t.removeLast() }
            return t
        }
        return norm(cfg.baseURL) == norm(p.baseURL) && cfg.model == p.model
    }

    private func apply(_ p: ModelPreset) {
        var c = store.config
        c.baseURL = p.baseURL
        c.model = p.model
        if !p.apiKey.isEmpty { c.apiKey = p.apiKey }
        c.extraHeaders = p.headers
        c.supportsEffort = p.supportsEffort
        store.update(c)
        model.focusInput()
    }

    private func iconButton(_ symbol: String, _ help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .frame(width: 22, height: 20)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(help)
    }

    private func pasteOrShot() {
        let pb = NSPasteboard.general
        if pb.types?.contains(.png) == true || pb.types?.contains(.tiff) == true {
            if model.addImage(from: pb) {
                model.focusInput()
                return
            }
        }
        AppHooks.screenshot?()
    }
}

// MARK: - Markdown

struct MDBlock: Identifiable {
    enum Kind {
        case paragraph(String)
        case heading(Int, String)
        case code(String, String)
        case bullets([String])
        case numbered([String])
        case quote(String)
    }
    let id = UUID()
    let kind: Kind
}

enum MDParser {
    static func parse(_ text: String) -> [MDBlock] {
        var blocks: [MDBlock] = []
        let lines = text.components(separatedBy: "\n")
        var i = 0
        var para: [String] = []

        func flushPara() {
            let s = para.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
            if !s.isEmpty { blocks.append(MDBlock(kind: .paragraph(s))) }
            para = []
        }

        while i < lines.count {
            let raw = lines[i]
            let t = raw.trimmingCharacters(in: .whitespaces)

            if t.hasPrefix("```") {
                flushPara()
                let lang = String(t.dropFirst(3)).trimmingCharacters(in: .whitespaces)
                var code: [String] = []
                i += 1
                while i < lines.count && !lines[i].trimmingCharacters(in: .whitespaces).hasPrefix("```") {
                    code.append(lines[i])
                    i += 1
                }
                blocks.append(MDBlock(kind: .code(code.joined(separator: "\n"), lang)))
                i += 1
                continue
            }

            if t.isEmpty {
                flushPara()
                i += 1
                continue
            }

            if t.hasPrefix("#") {
                let level = t.prefix(while: { $0 == "#" }).count
                var title = String(t.dropFirst(level)).trimmingCharacters(in: .whitespaces)
                if title.hasSuffix("#") { title = String(title.dropLast()).trimmingCharacters(in: .whitespaces) }
                if !title.isEmpty {
                    flushPara()
                    blocks.append(MDBlock(kind: .heading(min(level, 4), title)))
                    i += 1
                    continue
                }
            }

            if t.hasPrefix("- ") || t.hasPrefix("* ") || t.hasPrefix("• ") {
                flushPara()
                var items: [String] = []
                while i < lines.count {
                    let l = lines[i].trimmingCharacters(in: .whitespaces)
                    if l.hasPrefix("- ") || l.hasPrefix("* ") || l.hasPrefix("• ") {
                        items.append(String(l.dropFirst(2)))
                        i += 1
                    } else if !l.isEmpty && !l.hasPrefix("#") && (lines[i].hasPrefix("  ") || lines[i].hasPrefix("\t")) {
                        if !items.isEmpty { items[items.count - 1] += " " + l }
                        i += 1
                    } else { break }
                }
                blocks.append(MDBlock(kind: .bullets(items)))
                continue
            }

            if let _ = t.range(of: #"^\d+[.)]\s+"#, options: .regularExpression) {
                flushPara()
                var items: [String] = []
                while i < lines.count {
                    let l = lines[i].trimmingCharacters(in: .whitespaces)
                    if l.range(of: #"^\d+[.)]\s+"#, options: .regularExpression) != nil {
                        items.append(l)
                        i += 1
                    } else if !l.isEmpty && (lines[i].hasPrefix("  ") || lines[i].hasPrefix("\t")) {
                        if !items.isEmpty { items[items.count - 1] += " " + l }
                        i += 1
                    } else { break }
                }
                blocks.append(MDBlock(kind: .numbered(items)))
                continue
            }

            if t.hasPrefix("> ") {
                flushPara()
                var quote: [String] = []
                while i < lines.count {
                    let l = lines[i].trimmingCharacters(in: .whitespaces)
                    if l.hasPrefix(">") {
                        quote.append(String(l.dropFirst()).trimmingCharacters(in: .whitespaces))
                        i += 1
                    } else { break }
                }
                blocks.append(MDBlock(kind: .quote(quote.joined(separator: "\n"))))
                continue
            }

            para.append(raw)
            i += 1
        }
        flushPara()
        return blocks
    }

    static func inline(_ s: String) -> AttributedString {
        var attr = (try? AttributedString(
            markdown: s,
            options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        )) ?? AttributedString(s)
        for run in attr.runs {
            if run.inlinePresentationIntent == .code {
                attr[run.range].font = .system(size: 13.5, design: .monospaced)
            }
        }
        return attr
    }
}

struct MarkdownText: View {
    let text: String

    var body: some View {
        let blocks = MDParser.parse(text)
        VStack(alignment: .leading, spacing: 9) {
            ForEach(blocks) { block in
                switch block.kind {
                case .paragraph(let s):
                    Text(MDParser.inline(s))
                        .font(.system(size: 15))
                        .lineSpacing(3.5)
                        .fixedSize(horizontal: false, vertical: true)
                case .heading(let level, let s):
                    Text(MDParser.inline(s))
                        .font(.system(size: level <= 2 ? 18 : 16, weight: .semibold))
                        .padding(.top, 3)
                        .fixedSize(horizontal: false, vertical: true)
                case .code(let code, let lang):
                    CodeBlock(code: code, lang: lang)
                case .bullets(let items):
                    VStack(alignment: .leading, spacing: 5) {
                        ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                            HStack(alignment: .firstTextBaseline, spacing: 7) {
                                Text("•").font(.system(size: 14)).foregroundStyle(.secondary)
                                Text(MDParser.inline(item))
                                    .font(.system(size: 15))
                                    .lineSpacing(3)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                case .numbered(let items):
                    VStack(alignment: .leading, spacing: 5) {
                        ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                            Text(MDParser.inline(item))
                                .font(.system(size: 15))
                                .lineSpacing(3)
                                .fixedSize(horizontal: false, vertical: true)
                                .padding(.leading, 2)
                        }
                    }
                case .quote(let s):
                    HStack(alignment: .top, spacing: 9) {
                        RoundedRectangle(cornerRadius: 1.5)
                            .fill(Color.secondary.opacity(0.35))
                            .frame(width: 3)
                        Text(MDParser.inline(s))
                            .font(.system(size: 14.5))
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct CodeBlock: View {
    let code: String
    let lang: String
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(lang.isEmpty ? "code" : lang)
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundStyle(.secondary)
                Spacer()
                Button {
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.setString(code, forType: .string)
                    copied = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { copied = false }
                } label: {
                    Text(copied ? "已复制" : "复制")
                        .font(.system(size: 10.5))
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 10)
            .padding(.top, 7)
            .padding(.bottom, 5)

            ScrollView(.horizontal, showsIndicators: false) {
                Text(code)
                    .font(.system(size: 12.5, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(.horizontal, 10)
                    .padding(.bottom, 9)
            }
        }
        .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Color.primary.opacity(0.06)))
    }
}

// MARK: - 设置

struct SettingsView: View {
    @ObservedObject var store: AppStore
    @State private var draft: AppConfig = AppConfig()
    @State private var saved = false
    @State private var testStatus: String?
    @State private var testing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Form {
                Section("模型服务") {
                    TextField("Base URL", text: $draft.baseURL)
                        .textFieldStyle(.roundedBorder)
                    SecureField("API Key", text: $draft.apiKey)
                        .textFieldStyle(.roundedBorder)
                    TextField("模型 ID", text: $draft.model)
                        .textFieldStyle(.roundedBorder)
                    HStack {
                        Text("DeepSeek 官方：base 填 https://api.deepseek.com，模型填 deepseek-flash")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button(testing ? "测试中…" : "测试连接") { test() }
                            .disabled(testing || draft.apiKey.isEmpty)
                    }
                    if let s = testStatus {
                        Text(s).font(.system(size: 11))
                            .foregroundStyle(s.hasPrefix("✅") ? .green : .red)
                    }
                }

                Section("回答风格") {
                    Toggle("默认开启深度思考（更聪明、稍慢）", isOn: $draft.deepThink)
                    Toggle("发送 reasoning_effort 参数（DeepSeek/V4 系列需要）", isOn: $draft.supportsEffort)
                    TextEditor(text: $draft.systemPrompt)
                        .font(.system(size: 12))
                        .frame(height: 92)
                        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.secondary.opacity(0.25)))
                }

                Section("记录") {
                    Toggle("记录问答到工作区（一问一文件，按天分文件夹）", isOn: $draft.logEnabled)
                    TextField("目录", text: $draft.logDir)
                        .textFieldStyle(.roundedBorder)
                    HStack {
                        Text("每次问答写成「目录/日期/时间 摘要.md」，任何 Markdown 工具都能回看")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button("打开目录") {
                            let url = URL(fileURLWithPath: (draft.logDir as NSString).expandingTildeInPath)
                            try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
                            NSWorkspace.shared.open(url)
                        }
                    }
                }

                Section("唤醒") {
                    HStack {
                        Text("全局快捷键")
                        Spacer()
                        Text("⌥ Space").font(.system(size: 12, design: .monospaced))
                            .padding(.horizontal, 8).padding(.vertical, 3)
                            .background(RoundedRectangle(cornerRadius: 5).fill(Color.primary.opacity(0.07)))
                    }
                    Toggle("开机自动启动", isOn: Binding(
                        get: { LaunchAtLogin.isEnabled },
                        set: { LaunchAtLogin.set($0) }
                    ))
                    HStack {
                        Text("配置文件")
                        Spacer()
                        Button("在 Finder 中显示") {
                            NSWorkspace.shared.activateFileViewerSelecting([configURL()])
                        }
                    }
                }
            }
            .formStyle(.grouped)

            HStack {
                if saved {
                    Label("已保存", systemImage: "checkmark.circle.fill")
                        .font(.system(size: 12))
                        .foregroundStyle(.green)
                }
                Spacer()
                Button("保存") { save() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 16)
        }
        .frame(width: 540, height: 730)
        .onAppear { draft = store.config }
    }

    private func save() {
        store.update(draft)
        saved = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { saved = false }
    }

    private func test() {
        testing = true
        testStatus = nil
        var cfg = draft
        cfg.systemPrompt = "回复一个字：好"
        Task {
            do {
                var got = ""
                try await ChatEngine.stream(config: cfg, messages: [["role": "user", "content": "你好"]]) { t in
                    got += t
                }
                await MainActor.run {
                    testing = false
                    testStatus = "✅ 连接正常，返回：\(got.prefix(30))"
                }
            } catch {
                await MainActor.run {
                    testing = false
                    testStatus = "❌ \((error as? LocalizedError)?.errorDescription ?? error.localizedDescription)"
                }
            }
        }
    }
}
