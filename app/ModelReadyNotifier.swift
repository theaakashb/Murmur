import AppKit
import UserNotifications

/// Posts `ModelReady` notices. Permission is asked when a model download starts, never at launch.
@MainActor final class ModelReadyNotifier: NSObject, UNUserNotificationCenterDelegate {
    /// A click on the notice: bring Murmur forward.
    var onOpen: () -> Void = {}
    // Not available to a bare binary outside an app bundle.
    private var center: UNUserNotificationCenter? { Bundle.main.bundleURL.pathExtension == "app" ? .current() : nil }

    func attach() { center?.delegate = self }

    func requestPermission() { center?.requestAuthorization(options: [.alert]) { _, _ in } }

    func post(_ kind: ModelReady.Kind, model: String, shortcut: String) {
        guard let center else { return }
        center.getNotificationSettings { settings in
            let permitted = settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional
            DispatchQueue.main.async { MainActor.assumeIsolated {
                guard ModelReady.shouldPost(succeeded: true, permitted: permitted, appActive: NSApp.isActive, hasKeyWindow: NSApp.keyWindow != nil) else { return }
                let content = UNMutableNotificationContent()
                content.title = ModelReady.title; content.body = ModelReady.body(kind, model: model, shortcut: shortcut)
                center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
            } }
        }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler done: @escaping () -> Void) {
        DispatchQueue.main.async { MainActor.assumeIsolated { self.onOpen() }; done() }
    }
    // Murmur can be active with no key window; still show the banner then.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler done: @escaping (UNNotificationPresentationOptions) -> Void) {
        done([.banner])
    }
}
