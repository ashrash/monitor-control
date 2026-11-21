import Foundation

public enum ProfileOrigin {
    /// Shipped with the app (profiles/ folder in the repo).
    case builtIn
    /// Created by the setup wizard or by hand in the user's profiles folder.
    case user
    /// Generated on the fly for a monitor with no profile (standard MCCS).
    case fallback
}

/// Layer 3: one monitor model, its protocol, and its input codes. Stored as JSON and shared
/// verbatim with the Windows app. Schema: profiles/profile.schema.json. Mirrors `MonitorProfile.cs`,
/// including rejection of unknown properties (catches typos such as "productCode").
public struct MonitorProfile: Codable {
    public var schema: String?
    public var id: String = ""
    public var name: String = ""
    public var match = ProfileMatch()
    /// The "protocol" property in JSON (a keyword in Swift).
    public var protocolId: String = ""
    public var inputs: [ProfileInput] = []
    public var quirks: ProfileQuirks?
    public var notes: String?

    /// Not serialized.
    public var origin: ProfileOrigin = .builtIn
    /// File the profile came from. Not serialized.
    public var sourcePath: String?

    enum CodingKeys: String, CodingKey, CaseIterable {
        case schema = "$schema", id, name, match, protocolId = "protocol", inputs, quirks, notes
    }

    public init() {}

    public init(from decoder: Decoder) throws {
        try rejectUnknownKeys(decoder, allowed: CodingKeys.allCases.map(\.rawValue), type: "profile")
        let c = try decoder.container(keyedBy: CodingKeys.self)
        schema = try c.decodeIfPresent(String.self, forKey: .schema)
        id = try c.decodeIfPresent(String.self, forKey: .id) ?? ""
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        match = try c.decodeIfPresent(ProfileMatch.self, forKey: .match) ?? ProfileMatch()
        protocolId = try c.decodeIfPresent(String.self, forKey: .protocolId) ?? ""
        inputs = try c.decodeIfPresent([ProfileInput].self, forKey: .inputs) ?? []
        quirks = try c.decodeIfPresent(ProfileQuirks.self, forKey: .quirks)
        notes = try c.decodeIfPresent(String.self, forKey: .notes)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encodeIfPresent(schema, forKey: .schema)
        try c.encode(id, forKey: .id)
        try c.encode(name, forKey: .name)
        try c.encode(match, forKey: .match)
        try c.encode(protocolId, forKey: .protocolId)
        try c.encode(inputs, forKey: .inputs)
        try c.encodeIfPresent(quirks, forKey: .quirks)
        try c.encodeIfPresent(notes, forKey: .notes)
    }

    public var effectiveQuirks: ProfileQuirks { quirks ?? ProfileQuirks() }

    public func findInput(_ inputId: String) -> ProfileInput? {
        inputs.first { $0.id.caseInsensitiveCompare(inputId) == .orderedSame }
    }

    public func inputIndex(_ inputId: String) -> Int? {
        inputs.firstIndex { $0.id.caseInsensitiveCompare(inputId) == .orderedSame }
    }
}

public struct ProfileMatch: Codable {
    /// Three-letter EDID PNP id, e.g. "GSM" for LG.
    public var manufacturer: String = ""
    /// EDID product codes as hex strings ("0x774F").
    public var productCodes: [String]?
    /// Model token from the DDC/CI capabilities string ("UN880").
    public var capabilitiesModel: String?
    /// EDID monitor names. Weakest key: many LG models are all called "LG HDR 4K".
    public var modelNames: [String]?

    enum CodingKeys: String, CodingKey, CaseIterable {
        case manufacturer, productCodes, capabilitiesModel, modelNames
    }

    public init(manufacturer: String = "", productCodes: [String]? = nil, capabilitiesModel: String? = nil, modelNames: [String]? = nil) {
        self.manufacturer = manufacturer
        self.productCodes = productCodes
        self.capabilitiesModel = capabilitiesModel
        self.modelNames = modelNames
    }

    public init(from decoder: Decoder) throws {
        try rejectUnknownKeys(decoder, allowed: CodingKeys.allCases.map(\.rawValue), type: "match")
        let c = try decoder.container(keyedBy: CodingKeys.self)
        manufacturer = try c.decodeIfPresent(String.self, forKey: .manufacturer) ?? ""
        productCodes = try c.decodeIfPresent([String].self, forKey: .productCodes)
        capabilitiesModel = try c.decodeIfPresent(String.self, forKey: .capabilitiesModel)
        modelNames = try c.decodeIfPresent([String].self, forKey: .modelNames)
    }
}

public struct ProfileInput: Codable {
    public var id: String = ""
    public var name: String = ""
    /// Hex string, e.g. "0xD1".
    public var code: String = ""
    public var verified: Bool = false

    enum CodingKeys: String, CodingKey, CaseIterable {
        case id, name, code, verified
    }

    public init(id: String, name: String, code: String, verified: Bool = false) {
        self.id = id
        self.name = name
        self.code = code
        self.verified = verified
    }

    public init(from decoder: Decoder) throws {
        try rejectUnknownKeys(decoder, allowed: CodingKeys.allCases.map(\.rawValue), type: "input")
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(String.self, forKey: .id) ?? ""
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        code = try c.decodeIfPresent(String.self, forKey: .code) ?? ""
        verified = try c.decodeIfPresent(Bool.self, forKey: .verified) ?? false
    }

    /// Only valid on validated profiles.
    public var codeValue: UInt16 { Hex.parseUInt16(code) ?? 0 }
}

public struct ProfileQuirks: Codable {
    /// Extra attempts when the transport reports a failure.
    public var retries: Int = 1
    public var retryDelayMs: Int = 100
    public var postSwitchDelayMs: Int = 0
    /// The monitor ignores DDC/CI that arrives on an input it isn't showing (e.g. LG 32UN880). A
    /// computer can then switch the monitor away from itself, but not back; the other computer,
    /// the monitor's buttons, or the monitor's no-signal fallback has to bring it back.
    public var ignoresCommandsWhenInactive: Bool = false

    enum CodingKeys: String, CodingKey, CaseIterable {
        case retries, retryDelayMs, postSwitchDelayMs, ignoresCommandsWhenInactive
    }

    public init() {}

    public init(from decoder: Decoder) throws {
        try rejectUnknownKeys(decoder, allowed: CodingKeys.allCases.map(\.rawValue), type: "quirks")
        let c = try decoder.container(keyedBy: CodingKeys.self)
        retries = try c.decodeIfPresent(Int.self, forKey: .retries) ?? 1
        retryDelayMs = try c.decodeIfPresent(Int.self, forKey: .retryDelayMs) ?? 100
        postSwitchDelayMs = try c.decodeIfPresent(Int.self, forKey: .postSwitchDelayMs) ?? 0
        ignoresCommandsWhenInactive = try c.decodeIfPresent(Bool.self, forKey: .ignoresCommandsWhenInactive) ?? false
    }
}

struct AnyCodingKey: CodingKey {
    var stringValue: String
    var intValue: Int? { nil }

    init(stringValue: String) {
        self.stringValue = stringValue
    }

    init?(intValue: Int) {
        nil
    }
}

func rejectUnknownKeys(_ decoder: Decoder, allowed: [String], type: String) throws {
    let container = try decoder.container(keyedBy: AnyCodingKey.self)
    let unknown = container.allKeys.map(\.stringValue).filter { !allowed.contains($0) }.sorted()
    if !unknown.isEmpty {
        throw DecodingError.dataCorrupted(DecodingError.Context(
            codingPath: decoder.codingPath,
            debugDescription: "Unknown property '\(unknown.joined(separator: "', '"))' in \(type)."))
    }
}
