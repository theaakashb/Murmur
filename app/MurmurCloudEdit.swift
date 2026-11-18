import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Murmur Cloud voice edit: POST /v1/edit {text, instruction} -> {text}. The server builds the prompt.
// Not reachable from the app until the endpoint is deployed (see AIEngine availability in VoiceEdit.swift).

enum MurmurCloudEditFormat {
    struct Body: Encodable, Equatable { let text: String, instruction: String }

    static func makeRequest(text: String, instruction: String, key: APIKey) throws -> URLRequest {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        var request = URLRequest(url: MurmurCloud.baseURL.appendingPathComponent("v1/edit"))
        request.httpMethod = "POST"
        request.setValue("Bearer \(key.secret)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try encoder.encode(Body(text: text, instruction: instruction))
        return request
    }
}

struct MurmurCloudEditProvider: Sendable {
    let apiKey: APIKey
    var transport: HTTPTransport = URLSessionTransport()

    var disclosure: DataDisclosure {
        DataDisclosure(provider: .murmurCloud, stage: .cleanup, destinationHost: MurmurCloud.host,
                       leavesDevice: ["the selected text", "your spoken instruction"], staysOnDevice: ["audio", "history", "the dictionary"],
                       retentionNote: "")
    }

    /// A 404 (an older server) is a rejected request, never a retry on another route.
    func edit(text: String, instruction: String) async throws -> String {
        let request = try MurmurCloudEditFormat.makeRequest(text: text, instruction: instruction, key: apiKey)
        return try MurmurCloudCleanupFormat.parse(try await ProviderHTTP(provider: .murmurCloud, transport: transport, deadline: MurmurCloud.cleanupDeadline).execute(request))
    }
}
