import Foundation

/// The two writes bracket one nonthrowing insertion attempt. Never retry an uncertain paste.
enum Delivery {
    enum Result<Value> {
        case pendingSaveFailed
        case delivered(Value)
        case statusSaveFailed
    }

    static func deliver<Entry, Value>(_ entry: inout Entry, status: WritableKeyPath<Entry, String>,
                                      save: (Entry) throws -> Void,
                                      insert: (inout Entry) -> (status: String, value: Value)) -> Result<Value> {
        entry[keyPath: status] = "insertion-pending"
        do { try save(entry) } catch { return .pendingSaveFailed }
        let insertion = insert(&entry)
        entry[keyPath: status] = insertion.status
        do { try save(entry) } catch { return .statusSaveFailed }
        return .delivered(insertion.value)
    }

    static func statusSaveFailureMessage(saved: String) -> String {
        "\(saved) Delivery status could not be saved. Check the destination before pasting again."
    }
}
