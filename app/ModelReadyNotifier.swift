import AppKit
import UserNotifications

/// Posts `ModelReady` notices. Permission is asked when a model download starts, never at launch.
@MainActor final class ModelReadyNotifier: NSObject, UNUserNotificationCenterDelegate {
    /// A click on the notice: bring Murmur forward, at the Settings pane the notice names, if any.
    var onOpen: (SettingsPane?) -> Void = { _ in }
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

    /// Asks for permission if needed, then posts even while a Murmur window is open: the launch window does not show the model row.
    func postUpdate(models: [String], pane: SettingsPane) {
        guard let center else { return }
        center.requestAuthorization(options: [.alert]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = ModelReady.updateTitle; content.body = ModelReady.updateBody(models: models)
            content.userInfo = ["pane": pane.rawValue]
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler done: @escaping () -> Void) {
        let pane = (response.notification.request.content.userInfo["pane"] as? Int).flatMap(SettingsPane.init(rawValue:))
        DispatchQueue.main.async { MainActor.assumeIsolated { self.onOpen(pane) }; done() }
    }
    // Murmur can be active with no key window; still show the banner then.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler done: @escaping (UNNotificationPresentationOptions) -> Void) {
        done([.banner])
    }
}
