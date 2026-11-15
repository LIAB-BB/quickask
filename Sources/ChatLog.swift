import Foundation

/// 把每次完整问答写成独立 Markdown 文件，一天一个文件夹。
/// 目录结构：~/Documents/QuickAsk/2026-11-15/16-34 咖啡为什么提神.md
enum ChatLog {

    static func expand(_ path: String) -> URL {
        URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
    }

    static func append(config: AppConfig, question: String, answer: String, model: String, seconds: Double) {
        guard config.logEnabled else { return }
        let trimmedQ = question.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedA = answer.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedA.isEmpty else { return }

        let now = Date()
        let dateFmt = DateFormatter()
        dateFmt.locale = Locale(identifier: "en_US_POSIX")
        dateFmt.dateFormat = "yyyy-MM-dd"
        let timeFmt = DateFormatter()
        timeFmt.locale = Locale(identifier: "en_US_POSIX")
        timeFmt.dateFormat = "HH-mm"

        let base = expand(config.logDir)
        let day = dateFmt.string(from: now)
        let dir = base.appendingPathComponent(day, isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        let firstLine = trimmedQ
            .components(separatedBy: .newlines)
            .first(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty })?
            .trimmingCharacters(in: .whitespaces) ?? "问答"

        let title = firstLine.count > 40 ? String(firstLine.prefix(40)) + "…" : firstLine
        let slug = sanitize(String(firstLine.prefix(14)))
        let stamp = timeFmt.string(from: now)

        var url = dir.appendingPathComponent("\(stamp) \(slug).md")
        var index = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(stamp) \(slug)-\(index).md")
            index += 1
        }

        var body = "# \(title)\n\n"
        let timeText = DateFormatter.localizedString(from: now, dateStyle: .medium, timeStyle: .short)
        body += "- 时间：\(timeText)\n"
        body += "- 模型：\(model)\n"
        if seconds > 0 { body += String(format: "- 耗时：%.1fs\n", seconds) }
        body += "\n"
        if trimmedQ != firstLine || trimmedQ.count > title.count {
            body += "**问**：\n\n\(trimmedQ)\n\n"
        }
        body += "**答**：\n\n\(trimmedA)\n"

        try? body.data(using: .utf8)?.write(to: url, options: .atomic)
    }

    private static func sanitize(_ s: String) -> String {
        let banned = CharacterSet(charactersIn: "/\\:*?\"<>|#%")
        var out = String(s.unicodeScalars.map { banned.contains($0) ? " " : Character($0) })
        out = out.replacingOccurrences(of: "\t", with: " ")
        while out.contains("  ") { out = out.replacingOccurrences(of: "  ", with: " ") }
        out = out.trimmingCharacters(in: .whitespaces)
        return out.isEmpty ? "问答" : out
    }
}
