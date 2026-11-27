import AppKit
import SwiftUI

// Shared look: environment flags, palettes, type, logo, button/toggle styles and small shared controls.

// Only the isolated visual fixture supplies this override; production uses system preferences.
private struct UtilityOpaquePreviewKey: EnvironmentKey { static let defaultValue = false }
extension EnvironmentValues {
    var utilityOpaquePreview: Bool {
        get { self[UtilityOpaquePreviewKey.self] }
        set { self[UtilityOpaquePreviewKey.self] = newValue }
    }
}

// Glass belongs to the floating status and action layers; reading surfaces stay opaque.
struct UtilityGlass: ViewModifier {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.utilityOpaquePreview) private var opaquePreview
    var radius: CGFloat = 18

    func body(content: Content) -> some View {
        if opaquePreview || reduceTransparency || contrast == .increased {
            content.background(Color(nsColor: .windowBackgroundColor), in: RoundedRectangle(cornerRadius: radius))
        } else {
            if #available(macOS 26.0, *) { content.glassEffect(.regular, in: RoundedRectangle(cornerRadius: radius)) }
            else { content.background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: radius)) }
        }
    }
}

/// UtilityGlass for good news and tips: an accent glow from the leading corner and an accent rim.
/// Errors keep plain UtilityGlass. Without transparency the glow goes and the rim thickens to carry it.
struct AccentGlass: ViewModifier {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast
    var radius: CGFloat = 18

    func body(content: Content) -> some View {
        let plain = reduceTransparency || contrast == .increased
        content
            .modifier(UtilityGlass(radius: radius))
            .overlay {
                RoundedRectangle(cornerRadius: radius)
                    .fill(RadialGradient(colors: [Palette.accent.opacity(plain ? 0 : 0.2), .clear], center: .topLeading, startRadius: 0, endRadius: 200))
                    .allowsHitTesting(false)
            }
            .overlay { RoundedRectangle(cornerRadius: radius).strokeBorder(Palette.accent.opacity(0.4), lineWidth: plain ? 2 : 1).allowsHitTesting(false) }
    }
}

/// Neutral reading surfaces and the same royal-blue brand accent in both appearances.
enum Palette {
    private static func rgb(_ hex: Int) -> NSColor {
        NSColor(srgbRed: CGFloat(hex >> 16 & 0xFF) / 255, green: CGFloat(hex >> 8 & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
    static func dynamic(light: Int, dark: Int) -> NSColor {
        NSColor(name: nil) { $0.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? rgb(dark) : rgb(light) }
    }
    static let paperNS = dynamic(light: 0xFAFAFA, dark: 0x181818)
    static let paper = Color(nsColor: paperNS)
    static let ink = Color(nsColor: dynamic(light: 0x141414, dark: 0xFAFAFA))
    static let ink80 = ink.opacity(0.8)
    static let ink70 = ink.opacity(0.7)
    static let ink64 = ink.opacity(0.64)
    static let ink45 = ink.opacity(0.45)
    static let ink20 = ink.opacity(0.2)
    static let accent = Color(red: 77 / 255, green: 111 / 255, blue: 240 / 255)
    /// Small labels use neutral light text on dark surfaces to preserve contrast.
    static let accentInk = Color(nsColor: dynamic(light: 0x244BDF, dark: 0xFAFAFA))
    static let accent16 = Color(nsColor: dynamic(light: 0xEEEEEE, dark: 0x242424))
}

/// Settings and onboarding retain their scoped surface styling.
struct SettingsSurfaceKey: EnvironmentKey { static let defaultValue = false }
struct EditorialSurfaceKey: EnvironmentKey { static let defaultValue = false }
extension EnvironmentValues {
    var settingsSurface: Bool {
        get { self[SettingsSurfaceKey.self] }
        set { self[SettingsSurfaceKey.self] = newValue }
    }
    var editorialSurface: Bool {
        get { self[EditorialSurfaceKey.self] }
        set { self[EditorialSurfaceKey.self] = newValue }
    }
}

enum EditorialPalette {
    private static func color(_ light: Int, _ dark: Int) -> Color { Color(nsColor: Palette.dynamic(light: light, dark: dark)) }
    static let paper = color(0xFAFAFA, 0x181818)
    static let ink = color(0x202020, 0xFAFAFA)
    static let ink70 = color(0x555555, 0xBBBBBB)
    static let ink20 = color(0xCCCCCC, 0x555555)
    static let accent = color(0x4D6FF0, 0x4D6FF0)
    static let accentInk = Palette.accentInk
    static let accent16 = Palette.accent16
    static let sidebar = color(0xFFFFFF, 0x111111)
}

/// First-run guide colours, matching the Murmur website: ground, pale-blue pane, ink, muted ink, blue.
enum SetupPalette {
    private static func color(_ light: Int, _ dark: Int) -> Color { Color(nsColor: Palette.dynamic(light: light, dark: dark)) }
    static let ground = color(0xF4F4EF, 0x181818)
    static let pane = color(0xE5E9FF, 0x1C2238)
    static let ink = color(0x20221F, 0xF4F4EF)
    static let muted = color(0x50594E, 0xC4C9C2)
    static let blue = color(0x4568E8, 0x7F98FF)
}

/// A borderless panel that floats over every Space (the dictation bubble, the call offer, the live meeting panel). It never
/// becomes key unless `keyable`, so clicking it leaves the user's app in front.
class FloatingPanel: NSPanel {
    private let keyable: Bool
    override var canBecomeKey: Bool { keyable }
    override var canBecomeMain: Bool { false }
    init(title: String = "", size: NSSize = .zero, keyable: Bool = false) {
        self.keyable = keyable
        super.init(contentRect: NSRect(origin: .zero, size: size), styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        self.title = title
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false; backgroundColor = .clear; hasShadow = true
        hidesOnDeactivate = false; isReleasedWhenClosed = false
    }
}

func monoLabel(_ text: String, size: CGFloat = 12, color: Color = Palette.ink, plain: Bool = false) -> some View {
    Text(text).font(plain ? .system(size: size) : .system(size: size, design: .monospaced)).tracking(plain ? 0 : size * 0.08)
        .textCase(plain ? nil : .uppercase).foregroundStyle(color)
}

func helpText(_ text: String, size: CGFloat = 13, color: Color = Palette.ink70, system: Bool = false) -> some View {
    Text(text).font(system ? .system(size: size) : WindowType.body(size)).lineSpacing(2).foregroundStyle(color).fixedSize(horizontal: false, vertical: true)
}

func lede(_ text: String, color: Color = Palette.ink70) -> some View { helpText(text, size: 14, color: color) }

extension String {
    func isBlank(multiline: Bool = false) -> Bool { trimmingCharacters(in: multiline ? .whitespacesAndNewlines : .whitespaces).isEmpty }
}

func announce(_ text: String, priority: NSAccessibilityPriorityLevel = .high) {
    NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [.announcement: text, .priority: priority.rawValue])
}

/// Fonts ship with the app; the window never fetches typography from the network.
enum WindowType {
    private static let registered: Void = {
        for name in ["InstrumentSerif-Regular", "DMSans"] {
            if let url = Bundle.main.url(forResource: name, withExtension: "ttf", subdirectory: "Fonts") {
                CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
            }
        }
    }()
    static func display(_ size: CGFloat) -> Font {
        _ = registered
        return .custom("InstrumentSerif-Regular", size: size)
    }
    static func body(_ size: CGFloat, weight: Font.Weight = .regular) -> Font {
        _ = registered
        return .custom("DMSans-9ptRegular", size: size).weight(weight)
    }
}

/// The Murmur mark: five bars rising and falling like a voice, on the logo sheet's 96-unit grid.
enum Logo {
    static let bars: [CGRect] = [
        CGRect(x: 14, y: 36, width: 9, height: 24), CGRect(x: 29, y: 14, width: 9, height: 68),
        CGRect(x: 44, y: 30, width: 9, height: 36), CGRect(x: 59, y: 22, width: 9, height: 52),
        CGRect(x: 74, y: 38, width: 9, height: 20),
    ]
    static func path(in size: CGSize) -> Path {
        Path { path in bars.forEach { path.addRect($0.applying(CGAffineTransform(scaleX: size.width / 96, y: size.height / 96))) } }
    }
    /// One-colour menu bar mark; the template tint follows the menu bar.
    static let menuBarImage: NSImage = {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { rect in
            NSColor.black.setFill()
            bars.forEach { $0.applying(CGAffineTransform(scaleX: rect.width / 96, y: rect.height / 96)).fill() }
            return true
        }
        image.isTemplate = true; image.accessibilityDescription = "Murmur"
        return image
    }()
}

struct InkButtonStyle: ButtonStyle {
    enum Kind { case filled, outline, accent, plain }
    var kind: Kind = .filled
    @Environment(\.isEnabled) private var enabled
    @Environment(\.settingsSurface) private var settings
    @Environment(\.editorialSurface) private var editorial
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(settings ? .system(size: 13, weight: .medium) : WindowType.body(14, weight: .semibold)).lineLimit(1)
            .padding(.horizontal, kind == .plain ? 0 : (settings ? 12 : 18)).frame(minHeight: settings ? 30 : 36)
            .foregroundStyle(kind == .filled || kind == .accent ? Color.white : (editorial ? EditorialPalette.ink : Palette.ink))
            .background(kind == .filled || kind == .accent ? (Palette.accent) : Color.clear, in: RoundedRectangle(cornerRadius: settings ? 5 : (editorial ? 8 : 18)))
            .overlay { if kind == .outline { RoundedRectangle(cornerRadius: settings ? 5 : (editorial ? 8 : 18)).strokeBorder(settings ? Palette.ink20 : (editorial ? EditorialPalette.ink70 : Palette.ink), lineWidth: 1) } }
            .contentShape(Capsule())
            .opacity(enabled ? (configuration.isPressed ? 0.7 : 1) : 0.45)
    }
}

/// The system switch (focus ring, Increase Contrast, VoiceOver) with the label on the leading edge.
struct MurmurToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 24) {
            configuration.label.frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle()).onTapGesture { configuration.isOn.toggle() }
                .accessibilityHidden(true)
            Toggle(isOn: configuration.$isOn) { configuration.label }
                .toggleStyle(.switch).labelsHidden().tint(Palette.accent)
        }
        .frame(minHeight: 40)
    }
}

/// Names the window after the visible pane so VoiceOver, Mission Control and the Window menu show it.
struct WindowTitle: NSViewRepresentable {
    let title: String
    func makeNSView(context: Context) -> NSView { NSView() }
    func updateNSView(_ view: NSView, context: Context) {
        // The view joins its window after the first update.
        DispatchQueue.main.async { view.window?.title = title }
    }
}

struct ModelInfoIcons: View {
    let license: String?
    let details: String

    var body: some View {
        HStack(spacing: 4) {
            if let license {
                SettingInfoButton(text: license).accessibilityLabel("Model license")
            }
            if !details.isEmpty {
                SettingInfoButton(text: details, systemImage: "internaldrive")
                    .accessibilityLabel("Model size and details")
            }
        }
    }
}

struct ModelRowView: View {
    let slot: ModelSlot
    let title: AnyView
    let license: String?
    /// Shown instead of the status once the model is ready; nil shows nothing.
    let ready: AnyView?
    let status: (String) -> AnyView
    let progress: (Double) -> AnyView
    let message: (String) -> AnyView
    var statusTop: CGFloat = 5
    var spacing: CGFloat = 12
    /// Stacks the buttons under the name when the row is too narrow (the setup window).
    var narrow = false
    var extras: [AnyView] = []
    let download: () -> Void
    let cancel: () -> Void
    let remove: () -> Void

    var body: some View {
        let row = slot.row
        let info = VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 4) { title; ModelInfoIcons(license: license, details: slot.summary) }
            Group {
                if row.isReady { if let ready { ready } } else { status(row.statusText) }
            }.padding(.top, statusTop)
        }
        let actions = HStack(spacing: narrow ? 10 : 16) {
            if row.actions.contains(.download) { Button("Download…", action: download).buttonStyle(InkButtonStyle()).fixedSize() }
            if row.actions.contains(.retry) { Button("Retry…", action: download).buttonStyle(InkButtonStyle()).fixedSize() }
            if row.actions.contains(.reinstall) { Button("Reinstall…", action: download).buttonStyle(InkButtonStyle(kind: .outline)).fixedSize() }
            if row.actions.contains(.cancel) { Button("Cancel", action: cancel).buttonStyle(InkButtonStyle(kind: .outline)).fixedSize() }
            if row.actions.contains(.remove) { Button("Remove…", role: .destructive, action: remove).buttonStyle(InkButtonStyle(kind: .outline)).fixedSize() }
        }
        VStack(alignment: .leading, spacing: spacing) {
            if narrow {
                ViewThatFits(in: .horizontal) {
                    HStack(alignment: .center, spacing: 16) { info; Spacer(minLength: 8); actions }
                    VStack(alignment: .leading, spacing: 12) { info; actions }
                }
            } else {
                HStack(alignment: .center, spacing: 16) { info; Spacer(); actions }
            }
            ForEach(extras.indices, id: \.self) { extras[$0] }
            if let fraction = row.progress { progress(fraction) }
            if !slot.message.isEmpty { message(slot.message) }
        }
    }
}

/// Split in two so the compiler can type-check the chains.
struct SettingInfoButton: View {
    let text: String
    var systemImage = "questionmark.circle"
    @State private var isPresented = false

    var body: some View {
        Button { isPresented = true } label: {
            Image(systemName: systemImage)
                .font(WindowType.body(14))
                .foregroundStyle(Palette.ink70)
                .frame(width: 24, height: 24)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(text)
        .accessibilityLabel("More information")
        .accessibilityHint(text)
        .popover(isPresented: $isPresented, arrowEdge: .trailing) {
            Text(text).font(WindowType.body(13)).lineSpacing(2).fixedSize(horizontal: false, vertical: true)
                .padding(16).frame(width: 300, alignment: .leading)
        }
    }
}

func segmented<T: Hashable>(_ options: [(T, String)], _ selection: Binding<T>, size: CGFloat = 15, cloud: @escaping (T) -> Bool = { _ in false }) -> some View {
    HStack(spacing: 12) {
        ForEach(options.indices, id: \.self) { index in
            let (value, label) = options[index]
            let on = selection.wrappedValue == value
            Button { selection.wrappedValue = value } label: {
                HStack(spacing: 4) {
                    Text(label).lineLimit(1).minimumScaleFactor(0.75)
                    if cloud(value) { Image(systemName: "cloud.fill").font(.system(size: size - 2)).foregroundStyle(Palette.accent) }
                }
                    .font(WindowType.body(size, weight: .medium))
                    .frame(maxWidth: .infinity).frame(minHeight: 40)
                    .foregroundStyle(on ? Palette.ink : Palette.ink70)
                    .overlay(alignment: .bottom) { Rectangle().fill(on ? Palette.accent : Color.clear).frame(height: 2) }
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain).accessibilityAddTraits(on ? .isSelected : [])
            .accessibilityLabel(cloud(value) ? "\(label), runs on Murmur Cloud" : label)
        }
    }
}
