// deps: Tips Insights SettingsPanes
import Foundation

@main struct Gen {
    static func main() throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        print(String(decoding: try encoder.encode(["rungs": Tips.rungs]), as: UTF8.self))
    }
}
