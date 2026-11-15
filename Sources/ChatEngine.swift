import Foundation

struct QAError: LocalizedError {
    let message: String
    init(_ m: String) { message = m }
    var errorDescription: String? { message }
}

enum ChatEngine {
    /// opencode go 网关要求带会话头；每个进程一个即可
    static let sessionID = UUID().uuidString

    /// 不落盘：临时会话，内存缓存也关掉，磁盘上不留任何对话痕迹
    static let session: URLSession = {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.urlCache = nil
        cfg.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        cfg.httpCookieStorage = nil
        return URLSession(configuration: cfg)
    }()

    /// 流式请求 OpenAI Chat Completions 兼容接口。
    /// onText 在主线程被调用，逐段追加正文。
    static func stream(
        config: AppConfig,
        messages: [[String: Any]],
        onText: @escaping @MainActor (String) -> Void
    ) async throws {

        var base = config.baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !base.isEmpty else { throw QAError("还没填 API 地址，点菜单栏「设置」配置一下") }
        while base.hasSuffix("/") { base.removeLast() }
        if !base.hasSuffix("/chat/completions") {
            base += base.hasSuffix("/v1") ? "/chat/completions" : "/chat/completions"
        }
        guard let url = URL(string: base) else { throw QAError("API 地址格式不对：\(base)") }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.timeoutInterval = 300
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("Bearer \(config.apiKey)", forHTTPHeaderField: "Authorization")
        req.setValue("QuickAsk/1.0", forHTTPHeaderField: "User-Agent")
        for (k, v) in config.extraHeaders {
            req.setValue(v == "auto" ? ChatEngine.sessionID : v, forHTTPHeaderField: k)
        }

        var body: [String: Any] = [
            "model": config.model,
            "messages": messages,
            "stream": true,
            "max_tokens": 4096
        ]
        if let effort = config.effort { body["reasoning_effort"] = effort }
        req.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (bytes, response) = try await Self.session.bytes(for: req)
        guard let http = response as? HTTPURLResponse else { throw QAError("没有拿到服务器响应") }

        if http.statusCode != 200 {
            var data = Data()
            for try await b in bytes {
                data.append(b)
                if data.count > 3000 { break }
            }
            let raw = String(data: data, encoding: .utf8) ?? ""
            throw QAError("HTTP \(http.statusCode)：\(Self.friendly(raw))")
        }

        for try await line in bytes.lines {
            if Task.isCancelled { throw CancellationError() }
            guard line.hasPrefix("data:") else { continue }
            let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
            if payload.isEmpty { continue }
            if payload == "[DONE]" { break }
            guard let d = payload.data(using: .utf8),
                  let obj = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any] else { continue }

            if let err = obj["error"] as? [String: Any] {
                let msg = (err["message"] as? String) ?? "\(err)"
                throw QAError("接口报错：\(msg)")
            }
            guard let choices = obj["choices"] as? [[String: Any]],
                  let first = choices.first,
                  let delta = first["delta"] as? [String: Any] else { continue }

            if let c = delta["content"] as? String, !c.isEmpty {
                await MainActor.run { onText(c) }
            }
        }
    }

    private static func friendly(_ raw: String) -> String {
        guard let d = raw.data(using: .utf8),
              let obj = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any] else {
            return raw.isEmpty ? "（空响应）" : String(raw.prefix(300))
        }
        if let err = obj["error"] as? [String: Any] {
            if let m = err["message"] as? String { return m }
        }
        if let m = obj["message"] as? String { return m }
        return String(raw.prefix(300))
    }
}
