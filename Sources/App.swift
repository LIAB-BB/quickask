import SwiftUI
import AppKit

@main
struct QuickAskApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    var body: some Scene {
        Settings { EmptyView() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var store: AppStore!
    private var model: ChatModel!
    private var controller: PanelController!
    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private var keyMonitor: Any?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        store = AppStore.shared
        model = ChatModel(store: store)
        controller = PanelController(model: model, store: store)

        AppHooks.openSettings = { [weak self] in self?.openSettings() }
        AppHooks.screenshot = { [weak self] in self?.captureScreenshot() }

        HotKeyCenter.shared.onPress = { [weak self] in self?.controller.toggle() }
        HotKeyCenter.shared.registerOptionSpace()

        setupStatusItem()
        installKeyMonitor()
        handleLaunchArguments()
    }

    func applicationWillTerminate(_ notification: Notification) {
        HotKeyCenter.shared.unregister()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    // MARK: - 菜单栏

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            let img = NSImage(systemSymbolName: "text.bubble", accessibilityDescription: "随手问")
            img?.isTemplate = true
            button.image = img
            button.toolTip = "随手问 · ⌥Space"
        }
        let menu = NSMenu()
        menu.delegate = self
        menu.addItem(withTitle: "打开随手问", action: #selector(showPanel), keyEquivalent: "")
        menu.addItem(withTitle: "新对话", action: #selector(newChat), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "设置…", action: #selector(openSettings), keyEquivalent: ",")
        let launch = menu.addItem(withTitle: "开机自动启动", action: #selector(toggleLaunchAtLogin), keyEquivalent: "")
        launch.state = LaunchAtLogin.isEnabled ? .on : .off
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出随手问", action: #selector(quit), keyEquivalent: "q")
        statusItem.menu = menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        if let item = menu.items.first(where: { $0.action == #selector(toggleLaunchAtLogin) }) {
            item.state = LaunchAtLogin.isEnabled ? .on : .off
        }
    }

    @objc private func showPanel() { controller.show() }
    @objc private func newChat() { model.newConversation(); controller.show() }

    @objc private func toggleLaunchAtLogin() { LaunchAtLogin.set(!LaunchAtLogin.isEnabled) }

    @objc private func quit() { NSApp.terminate(nil) }

    // MARK: - 设置窗口

    @objc private func openSettings() {
        if settingsWindow == nil {
            let w = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 540, height: 730),
                styleMask: [.titled, .closable, .miniaturizable],
                backing: .buffered,
                defer: false
            )
            w.title = "随手问 · 设置"
            w.isReleasedWhenClosed = false
            let host = NSHostingView(rootView: SettingsView(store: store))
            host.translatesAutoresizingMaskIntoConstraints = false
            let container = NSView()
            container.addSubview(host)
            NSLayoutConstraint.activate([
                host.leadingAnchor.constraint(equalTo: container.leadingAnchor),
                host.trailingAnchor.constraint(equalTo: container.trailingAnchor),
                host.topAnchor.constraint(equalTo: container.topAnchor),
                host.bottomAnchor.constraint(equalTo: container.bottomAnchor)
            ])
            w.contentView = container
            w.center()
            settingsWindow = w
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    // MARK: - 截图

    private func captureScreenshot() {
        controller.hide()
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("quickask-shot-\(Int(Date().timeIntervalSince1970)).png")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = ["-i", "-x", tmp.path]
        p.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                guard let self else { return }
                if let img = NSImage(contentsOf: tmp) {
                    self.model.addImage(img)
                    try? FileManager.default.removeItem(at: tmp)
                }
                self.controller.show()
                self.model.focusInput()
            }
        }
        try? p.run()
    }

    // MARK: - 键盘

    private func installKeyMonitor() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            let mods = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
            let keyWindowIsPanel = (NSApp.keyWindow === self.controller.panel)

            if event.keyCode == 53, keyWindowIsPanel {   // Esc
                if self.model.isStreaming { self.model.cancel() }
                self.controller.hide()
                return nil
            }
            if mods.contains(.command), event.charactersIgnoringModifiers?.lowercased() == "n" {
                self.model.newConversation()
                self.controller.show()
                return nil
            }
            if mods.contains(.command), event.charactersIgnoringModifiers?.lowercased() == "k" {
                self.model.newConversation()
                return nil
            }
            return event
        }
    }

    // MARK: - 启动参数

    private func handleLaunchArguments() {
        let args = CommandLine.arguments
        if let idx = args.firstIndex(of: "--ask"), idx + 1 < args.count {
            model.input = args[idx + 1]
            controller.show()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { self.model.ask() }
            return
        }
        if args.contains("--show") || ProcessInfo.processInfo.environment["QUICKASK_SHOW"] == "1" {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { self.controller.show() }
        }
    }
}
