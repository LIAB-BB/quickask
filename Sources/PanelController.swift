import AppKit
import SwiftUI
import Combine

final class QuickPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class PanelController {
    let panel: QuickPanel
    private let hosting: NSHostingView<ChatPanelView>
    private let model: ChatModel
    private let store: AppStore
    private var cancelled: AnyCancellable?
    private var expanded = false

    private let width: CGFloat = 760
    private let collapsedHeight: CGFloat = 120

    init(model: ChatModel, store: AppStore) {
        self.model = model
        self.store = store

        panel = QuickPanel(
            contentRect: NSRect(x: 0, y: 0, width: width, height: 120),
            styleMask: [.borderless, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        panel.animationBehavior = .utilityWindow
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true

        let root = ChatPanelView(model: model, store: store)
        hosting = NSHostingView(rootView: root)
        hosting.translatesAutoresizingMaskIntoConstraints = false
        hosting.setContentHuggingPriority(.defaultLow, for: .horizontal)
        hosting.setContentHuggingPriority(.defaultLow, for: .vertical)
        hosting.setContentCompressionResistancePriority(.defaultLow, for: .vertical)

        let container = NSView()
        container.wantsLayer = true
        container.layer?.cornerRadius = 22
        container.layer?.cornerCurve = .continuous
        container.layer?.masksToBounds = true
        container.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor
        container.addSubview(hosting)
        NSLayoutConstraint.activate([
            hosting.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            hosting.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            hosting.topAnchor.constraint(equalTo: container.topAnchor),
            hosting.bottomAnchor.constraint(equalTo: container.bottomAnchor)
        ])
        panel.contentView = container

        cancelled = model.$messages
            .map { $0.count }
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                guard let self else { return }
                let want = self.model.hasConversation
                if want != self.expanded { self.setExpanded(want, animated: true) }
            }

        AppHooks.contentHeight = { [weak self] h in self?.contentHeightChanged(h) }
    }

    var isVisible: Bool { panel.isVisible }

    func toggle() {
        if panel.isVisible { hide() } else { show() }
    }

    func show() {
        setExpanded(model.hasConversation, animated: false)
        position()
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(hosting)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.02) { [weak self] in
            self?.model.focusInput()
        }
    }

    func hide() {
        panel.orderOut(nil)
        if NSApp.windows.contains(where: { $0.isVisible && $0 !== panel }) {
            NSApp.deactivate()
        } else {
            NSApp.hide(nil)
        }
    }

    private var pendingResize: DispatchWorkItem?

    func contentHeightChanged(_ contentHeight: CGFloat) {
        guard expanded, panel.isVisible else { return }
        let desired = min(maxExpandedHeight(), max(226, contentHeight + 2))
        guard abs(desired - panel.frame.height) > 6 else { return }
        pendingResize?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.expanded else { return }
            self.animateHeight(desired)
        }
        pendingResize = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12, execute: work)
    }

    private func animateHeight(_ h: CGFloat) {
        var f = panel.frame
        guard abs(f.height - h) > 4 else { return }
        let top = f.maxY
        f.size.height = h
        f.origin.y = top - h
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.12
            panel.animator().setFrame(f, display: true)
        }
    }

    private func maxExpandedHeight() -> CGFloat {
        let screen = panel.screen ?? NSScreen.main ?? NSScreen.screens[0]
        return min(680, max(360, screen.visibleFrame.height * 0.7))
    }

    private func expandedHeight() -> CGFloat {
        let screen = panel.screen ?? NSScreen.main ?? NSScreen.screens[0]
        return min(640, max(360, screen.visibleFrame.height * 0.62))
    }

    private func setExpanded(_ want: Bool, animated: Bool) {
        expanded = want
        let h = want ? expandedHeight() : collapsedHeight
        var f = panel.frame
        let top = f.maxY
        f.size.height = h
        f.origin.y = top - h
        if animated {
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.16
                ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
                panel.animator().setFrame(f, display: true)
            }
        } else {
            panel.setFrame(f, display: true)
        }
    }

    private func position() {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
        let vf = screen.visibleFrame
        let h = panel.frame.height
        let x = vf.midX - width / 2
        let top = vf.maxY - vf.height * 0.18
        panel.setFrame(NSRect(x: x, y: top - h, width: width, height: h), display: true)
    }
}
