// swift-tools-version:5.9
//
// Monitor Switcher for macOS (Apple Silicon).
//
// Layers mirror the Windows code in ../windows:
//   MonitorSwitcherCore  - packets, safety guard, protocols, profiles, switching logic (Foundation only)
//   MonitorSwitcherMac   - IOAVService transport, IORegistry display discovery, files, locks, logging
//   monitorswitcher      - command-line tool (same flags and exit codes as on Windows)
//   MonitorSwitcherApp   - SwiftUI menu bar app
//
// Run scripts/sync-shared.sh first: it copies the shared ../profiles and ../spec folders into the
// resource folders below (they are not committed).
import PackageDescription

let package = Package(
    name: "MonitorSwitcher",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "MonitorSwitcherCore", targets: ["MonitorSwitcherCore"]),
        .executable(name: "monitorswitcher", targets: ["monitorswitcher"]),
        .executable(name: "MonitorSwitcherApp", targets: ["MonitorSwitcherApp"]),
    ],
    targets: [
        .target(
            name: "MonitorSwitcherCore",
            resources: [.copy("Resources/profiles")]
        ),
        .target(
            name: "MonitorSwitcherMac",
            dependencies: ["MonitorSwitcherCore"],
            linkerSettings: [.linkedFramework("IOKit"), .linkedFramework("CoreFoundation")]
        ),
        .executableTarget(
            name: "monitorswitcher",
            dependencies: ["MonitorSwitcherCore", "MonitorSwitcherMac"]
        ),
        .executableTarget(
            name: "MonitorSwitcherApp",
            dependencies: ["MonitorSwitcherCore", "MonitorSwitcherMac"],
            linkerSettings: [
                .linkedFramework("Carbon"),
                .linkedFramework("ServiceManagement"),
                .linkedFramework("UserNotifications"),
            ]
        ),
        .testTarget(
            name: "MonitorSwitcherCoreTests",
            dependencies: ["MonitorSwitcherCore"],
            resources: [.copy("Resources/spec")]
        ),
    ]
)
