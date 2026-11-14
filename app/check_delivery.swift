import Foundation

@main struct DeliveryChecks {
    struct Entry { var status = "saved-not-inserted" }
    enum Failure: Error { case disk }

    static func main() {
        for failedSave in [1, 2, 0] {
            for insertionStatus in ["verified", "unverified", "failed"] {
                var entry = Entry(), events: [String] = [], writes = 0, attempts = 0
                let result = Delivery.deliver(&entry, status: \.status, save: { entry in
                    writes += 1; events.append(entry.status)
                    if writes == failedSave { throw Failure.disk }
                }, insert: { entry in
                    attempts += 1
                    assert(entry.status == "insertion-pending" && events == ["insertion-pending"])
                    events.append("insert")
                    return (insertionStatus, "result")
                })
                switch result {
                case .pendingSaveFailed:
                    assert(failedSave == 1 && attempts == 0 && writes == 1)
                case .statusSaveFailed:
                    assert(failedSave == 2 && attempts == 1 && writes == 2)
                case .delivered(let value):
                    assert(failedSave == 0 && attempts == 1 && writes == 2 && value == "result")
                }
                if failedSave != 1 {
                    assert(entry.status == insertionStatus)
                    assert(events == ["insertion-pending", "insert", insertionStatus])
                }
            }
        }
        assert(Delivery.statusSaveFailureMessage(saved: "Saved.") == "Saved. Delivery status could not be saved. Check the destination before pasting again.")
        print("Delivery checks passed")
    }
}
