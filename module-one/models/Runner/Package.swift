// swift-tools-version: 6.0
import PackageDescription
let package = Package(name: "ModelBench", platforms: [.macOS(.v14)], dependencies: [
    .package(url: "https://github.com/FluidInference/FluidAudio.git", revision: "0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7"),
    .package(url: "https://github.com/argmaxinc/argmax-oss-swift.git", revision: "9f99b5cb6a907730543823ad271b82532782a899")
], targets: [.executableTarget(name: "ModelBench", dependencies: [.product(name: "FluidAudio", package: "FluidAudio"), .product(name: "WhisperKit", package: "argmax-oss-swift")])])
