using System.Text.Json;
using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Ddc;

namespace MonitorSwitcher.Tests;

public class PacketTests
{
    public static TheoryData<string, byte, byte, ushort, string> Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.TestFile("spec", "packet-vectors.json")));
        var data = new TheoryData<string, byte, byte, ushort, string>();
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            data.Add(
                v.GetProperty("name").GetString()!,
                (byte)Hex.ParseUInt16(v.GetProperty("source").GetString()!),
                (byte)Hex.ParseUInt16(v.GetProperty("vcp").GetString()!),
                Hex.ParseUInt16(v.GetProperty("value").GetString()!),
                v.GetProperty("payload").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Payload_matches_shared_golden_vector(string name, byte source, byte vcp, ushort value, string expectedPayload)
    {
        var frame = new SetVcpFrame(source, vcp, value);
        Assert.True(Hex.ParseBytes(expectedPayload).SequenceEqual(frame.BuildPayload()), $"{name}: got {Hex.FormatBytes(frame.BuildPayload())}");
    }

    [Fact]
    public void Lg_usb_c_payload_matches_the_proof_of_concept()
    {
        // poc/test-lg-switch.ps1: chk = 0x6E ^ 0x50 ^ 0x84 ^ 0x03 ^ 0xF4 ^ 0x00 ^ value
        byte value = 0xD1;
        byte expectedChecksum = (byte)(0x6E ^ 0x50 ^ 0x84 ^ 0x03 ^ 0xF4 ^ 0x00 ^ value);

        var payload = new SetVcpFrame(0x50, 0xF4, value).BuildPayload();

        Assert.Equal(new byte[] { 0x84, 0x03, 0xF4, 0x00, value, expectedChecksum }, payload);
        Assert.Equal(0x9C, expectedChecksum);
    }

    [Theory]
    [InlineData(0x51, 0x60, 0x0012)]
    [InlineData(0x50, 0xF4, 0x00D1)]
    [InlineData(0x51, 0x10, 0xABCD)]
    public void Xor_of_a_whole_message_including_checksum_is_zero(byte source, byte vcp, ushort value)
    {
        var message = new SetVcpFrame(source, vcp, value).BuildMessage();
        byte xor = 0;
        foreach (var b in message)
        {
            xor ^= b;
        }
        Assert.Equal(0, xor);
    }

    [Fact]
    public void Message_starts_with_the_ddc_ci_device_address_then_source()
    {
        var message = new SetVcpFrame(0x50, 0xF4, 0xD1).BuildMessage();
        Assert.Equal(0x6E, message[0]);
        Assert.Equal(0x50, message[1]);
        Assert.Equal(8, message.Length);
    }

    [Fact]
    public void Sixteen_bit_values_are_sent_high_byte_first()
    {
        var payload = new SetVcpFrame(0x51, 0x10, 0x1234).BuildPayload();
        Assert.Equal(0x12, payload[3]);
        Assert.Equal(0x34, payload[4]);
    }

    [Fact]
    public void Checksum_helper_includes_device_and_source_address()
    {
        Assert.Equal((byte)(0x6E ^ 0x51), SetVcpFrame.ComputeChecksum(0x51, []));
        Assert.Equal((byte)(0x6E ^ 0x50 ^ 0xFF), SetVcpFrame.ComputeChecksum(0x50, [0xFF]));
    }

    [Theory]
    [InlineData("0xD1", 0xD1)]
    [InlineData("d1", 0xD1)]
    [InlineData("0x774F", 0x774F)]
    [InlineData(" 0X0f ", 0x0F)]
    public void Hex_parses_profile_style_values(string text, int expected)
    {
        Assert.True(Hex.TryParseUInt16(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0x10000")]
    [InlineData("zz")]
    public void Hex_rejects_invalid_values(string text) => Assert.False(Hex.TryParseUInt16(text, out _));
}
