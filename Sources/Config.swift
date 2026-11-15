import Foundation
import AppKit

// MARK: - 配置

struct ModelPreset: Codable, Identifiable, Hashable {
    var name: String
    var baseURL: String
    var model: String
    var apiKey: String = ""
    var headers: [String: String] = [:]
    var supportsEffort: Bool = false

    var id: String { "\(name)|\(baseURL)|\(model)" }
}

struct AppConfig: Codable {
    var baseURL: String = "https://api.deepseek.com"
    var apiKey: String = ""
    var model: String = "deepseek-flash"
    var deepThink: Bool = false
    var supportsEffort: Bool = true
    var extraHeaders: [String: String] = [:]
    var presets: [ModelPreset]? = nil
    var logEnabled: Bool = true
    var logDir: String = "~/Documents/QuickAsk"
    var systemPrompt: String = AppConfig.defaultPrompt

    var allPresets: [ModelPreset] {
        if let p = presets, !p.isEmpty { return p }
        return [
            ModelPreset(name: "DeepSeek V4.1 Flash · 直连", baseURL: "https://api.deepseek.com",
                        model: "deepseek-flash", apiKey: apiKey, supportsEffort: true),
            ModelPreset(name: "Qwen3.8 Flash · Go", baseURL: "https://opencode.ai/inference/go/openai/v1",
                        model: "qwen3.8-flash", headers: ["x-opencode-session": "auto"]),
            ModelPreset(name: "Claude Haiku 5.5 · Go", baseURL: "https://opencode.ai/inference/go/openai/v1",
                        model: "claude-haiku-5-5", headers: ["x-opencode-session": "auto"]),
            ModelPreset(name: "GPT-6 Luna · Go", baseURL: "https://opencode.ai/inference/go/openai/v1",
                        model: "gpt-6-luna", headers: ["x-opencode-session": "auto"]),
            ModelPreset(name: "Gemini 4 Argon · Go", baseURL: "https://opencode.ai/inference/go/openai/v1",
                        model: "gemini-4-argon", headers: ["x-opencode-session": "auto"])
        ]
    }

    enum CodingKeys: String, CodingKey {
        case baseURL, apiKey, model, deepThink, supportsEffort, extraHeaders, presets
        case systemPrompt, logEnabled, logDir
    }

    static let defaultPrompt = """
    你是用户 Mac 上的随身答疑助手「随手问」。
    默认用简体中文回答，除非用户使用其他语言提问。
    回答要短而准：先给结论，再给必要的细节或步骤；不要写引言和客套话。
    用户可能追问，请结合上下文回答。需要分点时用简短列表，代码用代码块。
    """

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        baseURL = try c.decodeIfPresent(String.self, forKey: .baseURL) ?? "https://api.deepseek.com"
        apiKey = try c.decodeIfPresent(String.self, forKey: .apiKey) ?? ""
        model = try c.decodeIfPresent(String.self, forKey: .model) ?? "deepseek-flash"
        deepThink = try c.decodeIfPresent(Bool.self, forKey: .deepThink) ?? false
        supportsEffort = try c.decodeIfPresent(Bool.self, forKey: .supportsEffort) ?? true
        extraHeaders = try c.decodeIfPresent([String: String].self, forKey: .extraHeaders) ?? [:]
        presets = try c.decodeIfPresent([ModelPreset].self, forKey: .presets)
        systemPrompt = try c.decodeIfPresent(String.self, forKey: .systemPrompt) ?? Self.defaultPrompt
        logEnabled = try c.decodeIfPresent(Bool.self, forKey: .logEnabled) ?? true
        logDir = try c.decodeIfPresent(String.self, forKey: .logDir) ?? "~/Documents/QuickAsk"
    }

    var displayModelName: String {
        if let p = allPresets.first(where: { $0.baseURL == baseURL && $0.model == model }) {
            return p.name
        }
        return model
    }

    var effort: String? {
        guard supportsEffort else { return nil }
        return deepThink ? "high" : "low"
    }
}

func configDirectory() -> URL {
    let base: URL
    if let custom = ProcessInfo.processInfo.environment["QUICKASK_HOME"], !custom.isEmpty {
        base = URL(fileURLWithPath: (custom as NSString).expandingTildeInPath)
    } else {
        base = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".config/quickask", isDirectory: true)
    }
    try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
    return base
}

func configURL() -> URL { configDirectory().appendingPathComponent("config.json") }

func loadConfig() -> AppConfig {
    guard let data = try? Data(contentsOf: configURL()) else { return AppConfig() }
    if let cfg = try? JSONDecoder().decode(AppConfig.self, from: data) { return cfg }
    return AppConfig()
}

@discardableResult
func saveConfig(_ cfg: AppConfig) -> Bool {
    let url = configURL()
    do {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        let data = try enc.encode(cfg)
        try data.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        return true
    } catch {
        NSLog("QuickAsk: 保存配置失败 \(error)")
        return false
    }
}

// MARK: - 全局状态

@MainActor
final class AppStore: ObservableObject {
    static let shared = AppStore()

    @Published var config: AppConfig

    init() {
        var cfg = loadConfig()
        // 允许用环境变量覆盖，便于测试
        let env = ProcessInfo.processInfo.environment
        if let v = env["QUICKASK_BASE_URL"], !v.isEmpty { cfg.baseURL = v }
        if let v = env["QUICKASK_API_KEY"], !v.isEmpty { cfg.apiKey = v }
        if let v = env["QUICKASK_MODEL"], !v.isEmpty { cfg.model = v }
        config = cfg
    }

    func update(_ cfg: AppConfig) {
        config = cfg
        saveConfig(cfg)
    }
}

// MARK: - 开机自启

import ServiceManagement

enum LaunchAtLogin {
    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    static var needsApproval: Bool {
        SMAppService.mainApp.status == .requiresApproval
    }

    static func set(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            NSLog("QuickAsk: 登录项已\(enabled ? "开启" : "关闭") status=\(SMAppService.mainApp.status.rawValue)")
        } catch {
            NSLog("QuickAsk: 设置登录项失败 \(error)")
        }
    }
}
