import XCTest
@testable import MonitorSwitcherCore

final class PacketTests: XCTestCase {
    private struct VectorFile: Decodable {
        struct Vector: Decodable {
            let name: String
            let source: String
            let vcp: String
            let value: String
            let payload: String
        }
        let vectors: [Vector]
    }

    func testPayloadsMatchSharedGoldenVectors() throws {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "packet-vectors", withExtension: "json", subdirectory: "spec"),
                                "Run scripts/sync-shared.sh first: spec/packet-vectors.json is missing.")
        let file = try JSONDecoder().decode(VectorFile.self, from: Data(contentsOf: url))
        XCTAssertFalse(file.vectors.isEmpty)
        for v in file.vectors {
            let frame = SetVcpFrame(sourceAddress: UInt8(try XCTUnwrap(Hex.parseUInt16(v.source))),
                                    vcpCode: UInt8(try XCTUnwrap(Hex.parseUInt16(v.vcp))),
                                    value: try XCTUnwrap(Hex.parseUInt16(v.value)))
            XCTAssertEqual(frame.buildPayload(), Hex.parseBytes(v.payload), v.name)
        }
    }

    func testLgUsbCPayloadMatchesTheProofOfConcept() {
        let value: UInt8 = 0xD1
        let checksum = UInt8(0x6E ^ 0x50 ^ 0x84 ^ 0x03 ^ 0xF4 ^ 0x00) ^ value
        XCTAssertEqual(SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1).buildPayload(), [0x84, 0x03, 0xF4, 0x00, value, checksum])
        XCTAssertEqual(checksum, 0x9C)
    }

    func testXorOfWholeMessageIsZero() {
        for (source, vcp, value) in [(UInt8(0x51), UInt8(0x60), UInt16(0x12)), (0x50, 0xF4, 0xD1), (0x51, 0x10, 0xABCD)] {
            let xor = SetVcpFrame(sourceAddress: source, vcpCode: vcp, value: value).buildMessage().reduce(UInt8(0), ^)
            XCTAssertEqual(xor, 0)
        }
    }

    func testMessageStartsWithDeviceAddressThenSource() {
        let message = SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1).buildMessage()
        XCTAssertEqual(message.count, 8)
        XCTAssertEqual(message[0], 0x6E)
        XCTAssertEqual(message[1], 0x50)
    }

    func testSixteenBitValuesAreHighByteFirst() {
        let payload = SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x10, value: 0x1234).buildPayload()
        XCTAssertEqual(payload[3], 0x12)
        XCTAssertEqual(payload[4], 0x34)
    }

    func testHexParsing() {
        XCTAssertEqual(Hex.parseUInt16("0xD1"), 0xD1)
        XCTAssertEqual(Hex.parseUInt16("d1"), 0xD1)
        XCTAssertEqual(Hex.parseUInt16("0x774F"), 0x774F)
        XCTAssertEqual(Hex.parseUInt16(" 0X0f "), 0x0F)
        for bad in ["", "0x", "0x10000", "zz", "+1"] {
            XCTAssertNil(Hex.parseUInt16(bad), bad)
        }
        XCTAssertEqual(Hex.format(0x0F), "0x0F")
        XCTAssertEqual(Hex.format(0x774F), "0x774F")
    }

    func testGetVcpRequestAndReplyParsing() throws {
        let request = DdcRead.getVcpRequest(vcp: 0x10)
        XCTAssertEqual(request, [0x82, 0x01, 0x10, 0x6E ^ 0x51 ^ 0x82 ^ 0x01 ^ 0x10])
        XCTAssertNoThrow(try DdcGuard.ensureMessage([0x6E, 0x51] + request))

        var reply: [UInt8] = [0x6E, 0x88, 0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x12]
        reply.append(reply.reduce(DdcConstants.replyChecksumSeed, ^))
        XCTAssertEqual(DdcRead.parseVcpReply(reply + [0], vcp: 0x10), VcpReading(current: 0x12, maximum: 0x64))
        XCTAssertEqual(DdcRead.parseVcpReply(Array(reply.dropFirst()), vcp: 0x10), VcpReading(current: 0x12, maximum: 0x64), "source byte may be missing")
        XCTAssertNil(DdcRead.parseVcpReply(reply, vcp: 0x60), "reply for another code")

        var corrupt = reply
        corrupt[9] ^= 0x01
        XCTAssertNil(DdcRead.parseVcpReply(corrupt, vcp: 0x10))

        var unsupported = reply
        unsupported[3] = 0x01
        unsupported[10] = unsupported[0..<10].reduce(DdcConstants.replyChecksumSeed, ^)
        XCTAssertNil(DdcRead.parseVcpReply(unsupported, vcp: 0x10))
    }
}

final class SafetyTests: XCTestCase {
    func testOnly0x6EIsAcceptedAsDeviceAddress() {
        for address: UInt8 in [0xA0, 0x50, 0xA1, 0x37, 0x00, 0x6F] {
            XCTAssertThrowsError(try DdcGuard.ensureDeviceAddress(address)) { error in
                XCTAssertEqual((error as? DdcError)?.kind, .safetyViolation)
            }
        }
        XCTAssertNoThrow(try DdcGuard.ensureDeviceAddress(0x6E))
    }

    func testUnknownSourceBytesAreRejected() {
        for source: UInt8 in [0x52, 0x6E, 0xA0, 0x00] {
            XCTAssertThrowsError(try DdcGuard.ensureFrame(SetVcpFrame(sourceAddress: source, vcpCode: 0x60, value: 0x11)))
        }
        XCTAssertNoThrow(try DdcGuard.ensureFrame(SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x60, value: 0x11)))
        XCTAssertNoThrow(try DdcGuard.ensureFrame(SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1)))
    }

    func testMessagesWithBadChecksumOrEdidAddressAreRejected() {
        var message = SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1).buildMessage()
        XCTAssertNoThrow(try DdcGuard.ensureMessage(message))
        message[message.count - 1] ^= 0x01
        XCTAssertThrowsError(try DdcGuard.ensureMessage(message))

        var edid = SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x60, value: 0x11).buildMessage()
        edid[0] = 0xA0
        XCTAssertThrowsError(try DdcGuard.ensureMessage(edid))
    }

    func testTransportSendRunsTheGuardBeforeTheDriverCall() {
        let transport = FakeTransport("raw", raw: true)
        let endpoint = transport.add(TestData.lg32un880)
        XCTAssertThrowsError(try transport.send(endpoint, SetVcpFrame(sourceAddress: 0x52, vcpCode: 0x60, value: 0x11)))
        XCTAssertTrue(transport.sent.isEmpty)
    }

    func testVcpOnlyTransportRefusesVendorFrames() {
        let transport = FakeTransport("vcp", raw: false)
        let endpoint = transport.add(TestData.lg32un880)
        XCTAssertThrowsError(try transport.send(endpoint, SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1))) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .unsupportedByTransport)
        }
        XCTAssertTrue(transport.sent.isEmpty)
    }

    func testEndpointFromAnotherTransportIsRefused() {
        let a = FakeTransport("a", raw: true)
        let b = FakeTransport("b", raw: true)
        let endpoint = a.add(TestData.lg32un880)
        XCTAssertThrowsError(try b.send(endpoint, SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x60, value: 0x11)))
    }
}

final class ParserTests: XCTestCase {
    func testLgCapabilitiesWithoutModelKeywordStillYieldTheModel() {
        let caps = CapabilitiesParser.parse(TestData.lg32un880Capabilities)
        XCTAssertEqual(caps.model, "UN880")
        XCTAssertEqual(caps.mccsVersion, "2.1")
        XCTAssertEqual(caps.valuesFor(0x60), [0x11, 0x12, 0x0F, 0x00])
        XCTAssertEqual(caps.valuesFor(0x14), [0x05, 0x08, 0x0B])
        XCTAssertTrue(caps.supportsVcp(0xF4))
        XCTAssertTrue(caps.supportsVcp(0xFF))
        XCTAssertFalse(caps.supportsVcp(0x61))
    }

    func testWellFormedCapabilities() {
        let caps = CapabilitiesParser.parse("(prot(monitor)type(LCD)model(U2720Q)cmds(01 02 03 07 0C E3 F3)vcp(02 04 10 12 60(0F 11 1B) D6(01 04 05))mccs_ver(2.1))")
        XCTAssertEqual(caps.model, "U2720Q")
        XCTAssertEqual(caps.valuesFor(0x60), [0x0F, 0x11, 0x1B])
        XCTAssertEqual(caps.valuesFor(0x10), [])
        XCTAssertTrue(caps.supportsVcp(0x10))
    }

    func testCapabilitiesToleratesMissingParenthesesAndGarbage() {
        let caps = CapabilitiesParser.parse("model(XYZ)vcp(60(11 12))\0")
        XCTAssertEqual(caps.model, "XYZ")
        XCTAssertEqual(caps.valuesFor(0x60), [0x11, 0x12])
        _ = CapabilitiesParser.parse("((((vcp(60(11")
    }

    func testEdidDecodesLg32un880() throws {
        let info = try XCTUnwrap(EdidParser.parse(TestData.edid(manufacturerRaw: 0x1E6D, product: 0x774F, serial: 384592, name: "LG HDR 4K")))
        XCTAssertEqual(info.manufacturerId, "GSM")
        XCTAssertEqual(info.productCode, 0x774F)
        XCTAssertEqual(info.serialNumber, 384592)
        XCTAssertEqual(info.monitorName, "LG HDR 4K")
        XCTAssertEqual(info.manufactureYear, 2023)
        XCTAssertTrue(info.checksumValid)
        XCTAssertEqual(MonitorIdentity.fromEdid(info).stableId, "GSM-774F-0005DE50")
    }

    func testEdidSerialStringIsUsedWhenNumericSerialIsZero() throws {
        let info = try XCTUnwrap(EdidParser.parse(TestData.edid(manufacturerRaw: 0x10AC, product: 0xA0F1, serial: 0, name: "DELL U2720Q", serialText: "ABC-123")))
        XCTAssertEqual(info.manufacturerId, "DEL")
        XCTAssertEqual(info.serialString, "ABC-123")
        XCTAssertEqual(MonitorIdentity.fromEdid(info).stableId, "DEL-A0F1-ABC123")
    }

    func testEdidRejectsDataWithoutHeader() {
        var edid = TestData.edid(manufacturerRaw: 0x1E6D, product: 0x774F, serial: 1, name: "X")
        edid[0] = 0x01
        XCTAssertNil(EdidParser.parse(edid))
        XCTAssertNil(EdidParser.parse([UInt8](repeating: 0, count: 10)))
    }

    func testPnpManufacturerIds() {
        XCTAssertEqual(EdidParser.decodeManufacturerId(0x1E6D), "GSM")
        XCTAssertEqual(EdidParser.decodeManufacturerId(0x10AC), "DEL")
        XCTAssertEqual(EdidParser.decodeManufacturerId(0x4C2D), "SAM")
        XCTAssertEqual(EdidParser.decodeManufacturerId(0x0610), "APP")
    }
}
