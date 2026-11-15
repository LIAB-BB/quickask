import Foundation
import AppKit
import Combine

struct Attachment: Identifiable, Equatable {
    let id = UUID()
    let data: Data
    let image: NSImage
}

struct UIMessage: Identifiable {
    enum Role: String { case user, assistant }
    let id = UUID()
    var role: Role
    var text: String
    var images: [Attachment] = []

    /// 转成接口消息体
    func apiDict() -> [String: Any] {
        if role == .assistant || images.isEmpty {
            return ["role": role.rawValue, "content": text]
        }
        var parts: [[String: Any]] = []
        if !text.isEmpty { parts.append(["type": "text", "text": text]) }
        for img in images {
            let b64 = img.data.base64EncodedString()
            parts.append([
                "type": "image_url",
                "image_url": ["url": "data:image/jpeg;base64,\(b64)"]
            ])
        }
        return ["role": role.rawValue, "content": parts]
    }
}

@MainActor
final class ChatModel: ObservableObject {
    @Published var messages: [UIMessage] = []
    @Published var input: String = ""
    @Published var streamingText: String = ""
    @Published var isStreaming: Bool = false
    @Published var errorText: String? = nil
    @Published var attachments: [Attachment] = []
    @Published var elapsed: Double = 0

    /// 让输入框重新获得焦点的信号
    @Published var focusTick: Int = 0

    let store: AppStore
    private var streamTask: Task<Void, Never>?
    private var timer: Timer?
    private var startedAt: Date?
    private var cancelled = false

    init(store: AppStore) { self.store = store }

    var hasConversation: Bool { !messages.isEmpty || isStreaming }

    var displayText: String {
        if isStreaming { return streamingText }
        return messages.last(where: { $0.role == .assistant })?.text ?? ""
    }

    var lastAssistant: UIMessage? { messages.last(where: { $0.role == .assistant }) }

    func ask() {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty || !attachments.isEmpty else { return }
        guard !isStreaming else { return }

        errorText = nil
        cancelled = false
        messages.append(UIMessage(role: .user, text: text, images: attachments))
        input = ""
        attachments = []
        streamingText = ""
        isStreaming = true
        elapsed = 0
        startTimer()

        streamTask = Task { [weak self] in
            await self?.run()
        }
    }

    private func run() async {
        let cfg = store.config
        guard !cfg.apiKey.isEmpty else {
            errorText = "还没填 API Key。点菜单栏图标 →「设置…」，或编辑 ~/.config/quickask/config.json"
            finishStream()
            return
        }

        var apiMessages: [[String: Any]] = [["role": "system", "content": cfg.systemPrompt]]
        for m in messages.suffix(12) { apiMessages.append(m.apiDict()) }

        do {
            try await ChatEngine.stream(config: cfg, messages: apiMessages) { [weak self] delta in
                guard let self else { return }
                self.streamingText += delta
            }
        } catch is CancellationError {
            // 用户主动打断
        } catch {
            if errorText == nil {
                errorText = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
        }
        finishStream()
    }

    private func finishStream() {
        let finished = streamingText
        if !finished.isEmpty, !cancelled {
            let question = messages.last(where: { $0.role == .user })?.text ?? ""
            ChatLog.append(config: store.config, question: question, answer: finished,
                          model: store.config.displayModelName, seconds: elapsed)
        }
        stopTimer()
        if !streamingText.isEmpty {
            messages.append(UIMessage(role: .assistant, text: streamingText))
        }
        streamingText = ""
        isStreaming = false
    }

    func cancel() {
        cancelled = true
        streamTask?.cancel()
        streamTask = nil
        // 保留已生成的部分
        finishStream()
    }

    func newConversation() {
        cancel()
        messages = []
        streamingText = ""
        input = ""
        attachments = []
        errorText = nil
        focusTick += 1
    }

    func focusInput() { focusTick += 1 }

    func copyLast() {
        let text = streamingText.isEmpty ? (lastAssistant?.text ?? "") : streamingText
        guard !text.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }

    func addImage(from pasteboard: NSPasteboard) -> Bool {
        if let data = pasteboard.data(forType: .png) ?? pasteboard.data(forType: .tiff),
           let img = NSImage(data: data) {
            return addImage(img)
        }
        for item in pasteboard.pasteboardItems ?? [] {
            if let fileURLString = item.string(forType: .fileURL),
               let url = URL(string: fileURLString),
               let img = NSImage(contentsOf: url) {
                return addImage(img)
            }
            if let types = item.types.first(where: { $0.rawValue.contains("png") || $0.rawValue.contains("tiff") || $0.rawValue.contains("jpeg") }),
               let data = item.data(forType: types), let img = NSImage(data: data) {
                return addImage(img)
            }
        }
        return false
    }

    @discardableResult
    func addImage(_ image: NSImage) -> Bool {
        guard let data = Self.compress(image) else { return false }
        attachments.append(Attachment(data: data, image: image))
        return true
    }

    func removeAttachment(_ att: Attachment) {
        attachments.removeAll { $0.id == att.id }
    }

    /// 调起系统截图（选区），成功后贴进输入区
    func captureScreenshot() {
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("quickask-shot-\(Int(Date().timeIntervalSince1970)).png")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = ["-i", "-x", tmp.path]
        p.terminationHandler = { _ in
            DispatchQueue.main.async {
                if let img = NSImage(contentsOf: tmp) {
                    self.addImage(img)
                    try? FileManager.default.removeItem(at: tmp)
                    self.focusInput()
                }
            }
        }
        try? p.run()
    }

    // MARK: - 计时

    private func startTimer() {
        stopTimer()
        startedAt = Date()
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, let s = self.startedAt else { return }
                self.elapsed = Date().timeIntervalSince(s)
            }
        }
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
        startedAt = nil
    }

    // MARK: - 图片压缩

    static func compress(_ image: NSImage, maxDim: CGFloat = 1568) -> Data? {
        var size = image.size
        if size.width <= 0 || size.height <= 0 {
            size = NSSize(width: 800, height: 600)
        }
        let scale = min(1, maxDim / max(size.width, size.height))
        let target = NSSize(width: max(1, size.width * scale), height: max(1, size.height * scale))
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(target.width), pixelsHigh: Int(target.height),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ), let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return nil }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        NSColor.white.setFill()
        NSRect(origin: .zero, size: target).fill()
        image.draw(in: NSRect(origin: .zero, size: target))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .jpeg, properties: [.compressionFactor: 0.82])
    }
}
