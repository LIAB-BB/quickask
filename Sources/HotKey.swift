import Foundation
import Carbon.HIToolbox
import AppKit

/// 全局热键（Carbon），无需辅助功能权限。
@MainActor
final class HotKeyCenter {
    static let shared = HotKeyCenter()

    var onPress: (() -> Void)?

    private var hotKeyRef: EventHotKeyRef?
    private var handlerRef: EventHandlerRef?

    private static let signature: OSType = 0x5141534B // 'QASK'

    func registerOptionSpace() {
        unregister()

        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )

        let callback: EventHandlerUPP = { _, _, _ -> OSStatus in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    HotKeyCenter.shared.onPress?()
                }
            }
            return noErr
        }

        InstallEventHandler(GetApplicationEventTarget(), callback, 1, &eventType, nil, &handlerRef)

        let hotKeyID = EventHotKeyID(signature: HotKeyCenter.signature, id: 1)
        let status = RegisterEventHotKey(
            UInt32(kVK_Space),
            UInt32(optionKey),
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &hotKeyRef
        )
        if status != noErr {
            NSLog("QuickAsk: 注册 ⌥Space 失败 status=\(status)")
        }
    }

    func unregister() {
        if let ref = hotKeyRef {
            UnregisterEventHotKey(ref)
            hotKeyRef = nil
        }
        if let h = handlerRef {
            RemoveEventHandler(h)
            handlerRef = nil
        }
    }
}
