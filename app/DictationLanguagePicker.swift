import SwiftUI

struct DictationLanguagePicker: View {
    @Binding var languages: [String]
    let onDevice: Bool
    var title = "What languages do you dictate in?"
    @State private var query = ""
    @FocusState private var searchFocused: Bool
    @Environment(\.locale) private var locale

    private static let suggested = ["en", "es", "fr", "hi", "ar"]
    private static let regions = [
        "ar": "SA", "bg": "BG", "cs": "CZ", "da": "DK", "de": "DE", "el": "GR", "en": "US",
        "es": "ES", "et": "EE", "fi": "FI", "fr": "FR", "hi": "IN", "hr": "HR", "hu": "HU",
        "it": "IT", "ja": "JP", "ko": "KR", "lt": "LT", "lv": "LV", "mt": "MT", "nl": "NL",
        "pl": "PL", "pt": "PT", "ro": "RO", "ru": "RU", "sk": "SK", "sl": "SI", "sv": "SE",
        "tr": "TR", "uk": "UA", "ur": "PK", "vi": "VN", "zh": "CN",
    ]

    private var results: [String] {
        let matches = DictationLanguages.matching(query, locale: locale)
        guard query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return matches }
        return Self.suggested + matches.filter { !Self.suggested.contains($0) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(WindowType.body(16, weight: .semibold))
            if !languages.isEmpty {
                Text(languages.map { "\(flag($0)) \(name($0))" }.joined(separator: "  ·  "))
                    .font(WindowType.body(13, weight: .medium))
                    .accessibilityLabel("Selected: \(languages.map(name).joined(separator: ", "))")
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").accessibilityHidden(true)
                TextField("Search languages", text: $query)
                    .textFieldStyle(.plain).focused($searchFocused)
                if !query.isEmpty {
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain).accessibilityLabel("Clear language search")
                }
            }
            .padding(8)
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
            .overlay {
                RoundedRectangle(cornerRadius: 6).strokeBorder(searchFocused ? Color.accentColor : .clear, lineWidth: 2)
                    .allowsHitTesting(false)
            }
            let matches = results
            if matches.isEmpty {
                Text("No matching languages").font(WindowType.body(13))
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(matches, id: \.self) { code in
                            let native = Locale(identifier: code).localizedString(forLanguageCode: code)
                            Toggle(isOn: Binding(get: { languages.contains(code) }, set: { selected in
                                if selected { languages.append(code) }
                                else if languages.count > 1 { languages.removeAll { $0 == code } }
                            })) {
                                HStack(spacing: 7) {
                                    Text(flag(code)).accessibilityHidden(true)
                                    Text(name(code))
                                    Spacer(minLength: 4)
                                    if let native, native.caseInsensitiveCompare(name(code)) != .orderedSame {
                                        Text(native).foregroundStyle(.secondary)
                                    }
                                }
                            }
                            .toggleStyle(.checkbox)
                            .font(WindowType.body(13))
                            .disabled(languages == [code])
                            .frame(minHeight: 26)
                        }
                    }.padding(.trailing, 10)
                }
                .frame(height: CGFloat(min(matches.count, 5)) * 26)
            }
            if onDevice, DictationLanguages(codes: Set(languages))?.localModel == .whisperTurbo {
                Label("Your selected languages need a larger speech model. Transcription may take longer on this Mac.", systemImage: "info.circle")
                    .font(WindowType.body(13))
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("dictation-model-speed-note")
            }
        }
        .accessibilityElement(children: .contain)
    }

    private func name(_ code: String) -> String { locale.localizedString(forLanguageCode: code) ?? code }

    private func flag(_ code: String) -> String {
        guard let region = Self.regions[code] else { return "🌐" }
        return String(String.UnicodeScalarView(region.unicodeScalars.compactMap { Unicode.Scalar($0.value + 127397) }))
    }
}
