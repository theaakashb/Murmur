// swift-tools-version: 6.1
import PackageDescription
// The managed local cleanup helper. Build with app/tools/build_cleanup_runner.sh, which swaps mlx-swift's Cmlx
// for the official prebuilt framework (no Metal compiler needed).
let package = Package(name: "LocalCleanupRunner", platforms: [.macOS(.v14)], dependencies: [
    .package(url: "https://github.com/ml-explore/mlx-swift-lm", revision: "25b00d4e22e61ec9c41efda47990cd2084ec87ff"),
], targets: [.executableTarget(name: "LocalCleanupRunner", dependencies: [
    .product(name: "MLXLLM", package: "mlx-swift-lm"),
    .product(name: "MLXLMCommon", package: "mlx-swift-lm"),
], swiftSettings: [.swiftLanguageMode(.v5)])])
