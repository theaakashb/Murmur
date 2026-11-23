import Foundation

/// Published model coverage. Language IDs are separate from any flags shown by the picker.
struct DictationLanguages: Equatable, Sendable {
    enum LocalModel: Equatable, Sendable {
        case parakeetEnglish, parakeetTDT, whisperTurbo
    }

    // NVIDIA Parakeet TDT v3 model card.
    static let parakeet: Set<String> = [
        "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hr", "hu", "it",
        "lt", "lv", "mt", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "uk",
    ]

    // Whisper large-v3-turbo generation_config.json language tokens.
    static let whisper: Set<String> = [
        "af", "am", "ar", "as", "az", "ba", "be", "bg", "bn", "bo", "br", "bs", "ca",
        "cs", "cy", "da", "de", "el", "en", "es", "et", "eu", "fa", "fi", "fo", "fr",
        "gl", "gu", "ha", "haw", "he", "hi", "hr", "ht", "hu", "hy", "id", "is", "it",
        "ja", "jw", "ka", "kk", "km", "kn", "ko", "la", "lb", "ln", "lo", "lt", "lv",
        "mg", "mi", "mk", "ml", "mn", "mr", "ms", "mt", "my", "ne", "nl", "nn", "no", "oc",
        "pa", "pl", "ps", "pt", "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so",
        "sq", "sr", "su", "sv", "sw", "ta", "te", "tg", "th", "tk", "tl", "tr", "tt",
        "uk", "ur", "uz", "vi", "yi", "yo", "yue", "zh",
    ]

    static let availableCodes = parakeet.union(whisper)

    /// Match the setup language, the language's own name, and its decoder code.
    static func matching(_ query: String, locale: Locale = .current) -> [String] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive, .widthInsensitive]
        let names = availableCodes.map { code in
            let name = locale.localizedString(forLanguageCode: code) ?? code
            let native = Locale(identifier: code).localizedString(forLanguageCode: code) ?? name
            return (code: code, name: name, native: native)
        }
        return names.filter { entry in
            query.isEmpty || [entry.code, entry.name, entry.native].contains {
                $0.range(of: query, options: options, locale: locale) != nil
            }
        }.sorted { lhs, rhs in
            let order = lhs.name.compare(rhs.name, options: options, locale: locale)
            return order == .orderedSame ? lhs.code < rhs.code : order == .orderedAscending
        }.map(\.code)
    }

    let codes: Set<String>

    init?(codes: Set<String>) {
        guard !codes.isEmpty, codes.isSubset(of: Self.availableCodes) else { return nil }
        self.codes = codes
    }

    /// An absent preference represents an existing English-only setup.
    static let english = DictationLanguages(codes: ["en"])!

    /// Prefer the smallest model that covers the complete selection.
    var localModel: LocalModel {
        if self == Self.english { return .parakeetEnglish }
        if codes.isSubset(of: Self.parakeet) { return .parakeetTDT }
        return .whisperTurbo
    }

    var cloudHint: String? { self == Self.english ? "en" : nil }
}
