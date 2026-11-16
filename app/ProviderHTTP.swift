import Foundation
#if canImport(os)
import os
#endif
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// HTTP transport, deadline, error classification and multipart bodies shared by every provider.


struct HTTPResult: Sendable {
    let status: Int
    let headers: [String: String]
    let body: Data

    init(status: Int, headers: [String: String] = [:], body: Data = Data()) {
        self.status = status; self.headers = headers; self.body = body
    }

    /// Case-insensitive header lookup.
    func header(_ name: String) -> String? {
        let lower = name.lowercased()
        return headers.first { $0.key.lowercased() == lower }?.value
    }
}

protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> HTTPResult
}

/// Sensitive requests refuse redirects, cookies, cached data and oversized responses.
final class URLSessionTransport: HTTPTransport, @unchecked Sendable {
    private let configuration: URLSessionConfiguration
    private let maximumResponseBytes: Int

    init(maximumResponseBytes: Int = 8 * 1024 * 1024) {
        self.maximumResponseBytes = max(1, maximumResponseBytes)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        self.configuration = configuration
    }

    func send(_ request: URLRequest) async throws -> HTTPResult {
        let holder = DataTaskHolder()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<HTTPResult, Error>) in
                let receiver = ResponseReceiver(limit: maximumResponseBytes) { continuation.resume(with: $0) }
                let session = URLSession(configuration: configuration, delegate: receiver, delegateQueue: nil)
                holder.start(session.dataTask(with: request))
            }
        } onCancel: { holder.cancel() }
    }
}

/// Each session serializes its callbacks; each request owns its receiver and bounded buffer.
private final class ResponseReceiver: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let limit: Int
    let complete: (Result<HTTPResult, Error>) -> Void
    private var response: HTTPURLResponse?
    private var body = Data()
    private var failure: Error?

    init(limit: Int, complete: @escaping (Result<HTTPResult, Error>) -> Void) {
        self.limit = limit; self.complete = complete
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        self.response = response as? HTTPURLResponse
        if response.expectedContentLength > Int64(limit) {
            failure = URLError(.dataLengthExceedsMaximum); completionHandler(.cancel)
        } else { completionHandler(.allow) }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        guard data.count <= limit - body.count else {
            failure = URLError(.dataLengthExceedsMaximum); dataTask.cancel(); return
        }
        body.append(data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        defer { session.finishTasksAndInvalidate() }
        if let error = failure ?? error { complete(.failure(error)); return }
        guard let response else { complete(.failure(URLError(.badServerResponse))); return }
        var headers: [String: String] = [:]
        for (key, value) in response.allHeaderFields { if let key = key as? String { headers[key] = "\(value)" } }
        complete(.success(HTTPResult(status: response.statusCode, headers: headers, body: body)))
    }
}

private final class DataTaskHolder: @unchecked Sendable {
    private let lock = NSLock()
    private var task: URLSessionDataTask?
    private var cancelled = false

    func start(_ newTask: URLSessionDataTask) {
        lock.lock(); task = newTask; let wasCancelled = cancelled; lock.unlock()
        newTask.resume()
        if wasCancelled { newTask.cancel() }
    }

    func cancel() {
        lock.lock(); cancelled = true; let current = task; lock.unlock()
        current?.cancel()
    }
}

/// Executes one request with a total deadline and maps transport failures to `ProviderError`.
struct ProviderHTTP: Sendable {
    let provider: ProviderID
    let transport: HTTPTransport
    let deadline: TimeInterval

    func execute(_ request: URLRequest) async throws -> HTTPResult {
        if Task.isCancelled { throw ProviderError.cancelled }
        var timed = request
        timed.timeoutInterval = deadline
        let finalRequest = timed
        let transport = self.transport
        let provider = self.provider
        let deadline = self.deadline
        do {
            let result = try await withThrowingTaskGroup(of: HTTPResult?.self) { group -> HTTPResult in
                group.addTask { try await transport.send(finalRequest) }
                group.addTask {
                    try await Task.sleep(nanoseconds: UInt64(max(deadline, 0.001) * 1_000_000_000))
                    return nil
                }
                defer { group.cancelAll() }
                guard let first = try await group.next(), let result = first else { throw ProviderError.timeout(provider) }
                return result
            }
            if Task.isCancelled { throw ProviderError.cancelled }
            return result
        } catch let error as ProviderError {
            if Task.isCancelled { throw ProviderError.cancelled }
            throw error
        } catch {
            if Task.isCancelled || error is CancellationError { throw ProviderError.cancelled }
            throw ProviderErrorClassifier.classify(provider: provider, transportError: error, url: finalRequest.url)
        }
    }
}


enum ProviderErrorClassifier {
    /// `url` names the address of a local server that refused the connection; any provider aimed at a
    /// loopback address (Ollama, LM Studio) gets that error, not only the legacy local-server id.
    static func classify(provider: ProviderID, transportError error: Error, url: URL? = nil) -> ProviderError {
        if error is CancellationError { return .cancelled }
        if let urlError = error as? URLError {
            switch urlError.code {
            case .cancelled: return .cancelled
            case .timedOut: return .timeout(provider)
            case .dataLengthExceedsMaximum: return .malformedResponse(provider)
            case .cannotConnectToHost where provider == .localServer || url.map(LocalEndpoint.locality(of:)) == .onDevice:
                return .serverNotRunning(provider, address: url.map(LocalEndpoint.address(of:)) ?? "the configured address")
            default: return .network(provider)
            }
        }
        return .network(provider)
    }

    /// Classifies a non-success HTTP result, or a 200 that carries an error object (some gateways
    /// report errors raised mid-generation that way).
    static func classify(provider: ProviderID, result: HTTPResult, now: Date = Date()) -> ProviderError {
        let info = ErrorBody(result.body)
        var status = result.status
        if (200..<300).contains(status) {
            guard let code = info.numericCode, code >= 400 else { return .server(provider, status: nil) }
            status = code
        }
        let retryAfter = parseRetryAfter(result.header("Retry-After"), now: now) ?? info.retryDelay
        let text = info.searchableText

        switch status {
        case 401, 403:
            return .authentication(provider)
        case 402:
            return .quota(provider)
        case 413:
            return .payloadTooLarge(provider, limitBytes: nil)
        case 408, 504:
            return .timeout(provider)
        case 300..<400:
            // Redirects are not followed for local servers; any 3xx here is unusable.
            return .rejected(provider, status: status)
        case 429:
            if provider == .gemini && isDailyQuota(text) { return .quota(provider) }
            return .rateLimited(provider, retryAfter: retryAfter)
        case 400:
            if provider == .gemini {
                if text.contains("api_key_invalid") || text.contains("api key not valid") || text.contains("api key expired") { return .authentication(provider) }
                if text.contains("failed_precondition") && text.contains("billing") { return .quota(provider) }
            }
            return .rejected(provider, status: status)
        case 400..<500:
            return .rejected(provider, status: status)
        default:
            return .server(provider, status: status)
        }
    }

    static func isDailyQuota(_ lowercasedText: String) -> Bool {
        // Not "billing": Google's per-minute 429 message also says "check your plan and billing details".
        ["quota_exceeded", "perday", "per day", "per_day", "daily", "spend cap", "spending cap", "spend limit"].contains { lowercasedText.contains($0) }
    }

    /// `Retry-After` as delta-seconds or an HTTP-date.
    static func parseRetryAfter(_ value: String?, now: Date) -> TimeInterval? {
        guard let raw = value?.trimmingCharacters(in: .whitespaces), !raw.isEmpty else { return nil }
        if let seconds = Double(raw) { return seconds.isFinite && seconds >= 0 && seconds < Double(Int.max) ? seconds : nil }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        guard let date = formatter.date(from: raw) else { return nil }
        return max(0, date.timeIntervalSince(now))
    }

    /// Google duration string such as "30s" or "1.5s".
    static func parseGoogleDuration(_ value: String) -> TimeInterval? {
        guard value.hasSuffix("s"), let seconds = Double(value.dropLast()), seconds.isFinite, seconds >= 0, seconds < Double(Int.max) else { return nil }
        return seconds
    }
}

/// Extracts classification signals from the error body shapes of all three providers.
/// It is only matched against fixed keywords; none of its text is shown to the user.
struct ErrorBody {
    var numericCode: Int?
    var codeString: String?
    var status: String?
    var type: String?
    var message: String?
    var reasons: [String] = []
    var retryDelay: TimeInterval?

    init(_ data: Data) {
        guard let object = ErrorBody.topLevelObject(data),
              let error = object["error"] as? [String: Any] else { return }
        if let code = error["code"] as? Int { numericCode = code }
        else if let code = error["code"] as? String { codeString = code; numericCode = Int(code) }
        status = error["status"] as? String
        type = error["type"] as? String
        message = error["message"] as? String
        if let details = error["details"] as? [[String: Any]] {
            for detail in details {
                if let reason = detail["reason"] as? String { reasons.append(reason) }
                if let delay = detail["retryDelay"] as? String { retryDelay = ProviderErrorClassifier.parseGoogleDuration(delay) }
                if let violations = detail["violations"] as? [[String: Any]] {
                    for violation in violations {
                        if let quotaId = violation["quotaId"] as? String { reasons.append(quotaId) }
                    }
                }
            }
        }
        if let metadata = error["metadata"] as? [String: Any] {
            if let reasons = metadata["reasons"] as? [String] { self.reasons.append(contentsOf: reasons) }
            if metadata["flagged_input"] != nil { self.reasons.append("flagged") }
        }
    }

    var searchableText: String {
        ([codeString, status, type, message].compactMap { $0 } + reasons).joined(separator: " ").lowercased()
    }

    /// The top-level JSON object. Google's front end wraps some error bodies in a one-element
    /// array (`[{"error": {...}}]`, observed on `POST` and `GET /v1beta/interactions` on
    /// 2026-10-03), so a single-object array is unwrapped.
    static func topLevelObject(_ data: Data) -> [String: Any]? {
        let parsed = try? JSONSerialization.jsonObject(with: data)
        if let object = parsed as? [String: Any] { return object }
        if let array = parsed as? [Any], array.count == 1, let object = array[0] as? [String: Any] { return object }
        return nil
    }
}

enum Multipart {
    static func makeBoundary() -> String { "MurmurBoundary-" + UUID().uuidString }

    /// multipart/form-data body with text fields followed by one file part.
    static func body(boundary: String, fields: [(name: String, value: String)], fileField: String, filename: String, mimeType: String, fileData: Data) -> Data {
        var body = Data()
        func append(_ string: String) { body.append(Data(string.utf8)) }
        for field in fields {
            append("--\(boundary)\r\n")
            append("Content-Disposition: form-data; name=\"\(sanitize(field.name))\"\r\n\r\n")
            append(field.value)
            append("\r\n")
        }
        append("--\(boundary)\r\n")
        append("Content-Disposition: form-data; name=\"\(sanitize(fileField))\"; filename=\"\(sanitize(filename))\"\r\n")
        append("Content-Type: \(sanitize(mimeType))\r\n\r\n")
        body.append(fileData)
        append("\r\n--\(boundary)--\r\n")
        return body
    }

    /// Prevents header injection through names, filenames or MIME types.
    static func sanitize(_ value: String) -> String {
        String(value.unicodeScalars.filter { $0 != "\"" && $0 != "\r" && $0 != "\n" && $0 != "\\" }.map(Character.init))
    }
}

func validationResult(provider: ProviderID, _ result: HTTPResult) throws {
    guard (200..<300).contains(result.status) else { throw ProviderErrorClassifier.classify(provider: provider, result: result) }
}
