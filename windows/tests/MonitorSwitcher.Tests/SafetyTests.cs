using MonitorSwitcher.Core.Ddc;

namespace MonitorSwitcher.Tests;

public class SafetyTests
{
    [Theory]
    [InlineData(0xA0)] // EDID EEPROM, 8-bit
    [InlineData(0x50)] // EDID EEPROM, 7-bit
    [InlineData(0xA1)]
    [InlineData(0x37)] // 7-bit DDC/CI address must not be passed where the 8-bit one is expected
    [InlineData(0x00)]
    [InlineData(0x6F)]
    public void Only_0x6E_is_accepted_as_device_address(byte address)
    {
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureDeviceAddress(address));
    }

    [Fact]
    public void Ddc_ci_device_address_is_accepted() => DdcGuard.EnsureDeviceAddress(0x6E);

    [Theory]
    [InlineData(0x52)]
    [InlineData(0x6E)]
    [InlineData(0xA0)]
    [InlineData(0x00)]
    public void Frames_with_unknown_source_bytes_are_rejected(byte source)
    {
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureFrame(new SetVcpFrame(source, 0x60, 0x11)));
    }

    [Theory]
    [InlineData(0x51)]
    [InlineData(0x50)]
    public void Standard_and_lg_source_bytes_are_allowed(byte source) =>
        DdcGuard.EnsureFrame(new SetVcpFrame(source, 0x60, 0x11));

    [Fact]
    public void Message_with_wrong_checksum_is_rejected()
    {
        var message = new SetVcpFrame(0x50, 0xF4, 0xD1).BuildMessage();
        message[^1] ^= 0x01;
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureMessage(message));
    }

    [Fact]
    public void Message_addressed_to_the_edid_eeprom_is_rejected()
    {
        var message = new SetVcpFrame(0x51, 0x60, 0x11).BuildMessage();
        message[0] = 0xA0;
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureMessage(message));
    }

    [Fact]
    public void Valid_message_passes() => DdcGuard.EnsureMessage(new SetVcpFrame(0x50, 0xF4, 0xD1).BuildMessage());

    [Fact]
    public void Transport_send_runs_the_guard_before_the_driver_call()
    {
        var transport = new FakeTransport("raw", raw: true);
        var endpoint = transport.Add(TestData.Lg32Un880);

        Assert.Throws<DdcSafetyException>(() => transport.Send(endpoint, new SetVcpFrame(0x52, 0x60, 0x11)));
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Vcp_only_transport_refuses_vendor_frames()
    {
        var transport = new FakeTransport("vcp", raw: false);
        var endpoint = transport.Add(TestData.Lg32Un880);

        var ex = Assert.Throws<DdcException>(() => transport.Send(endpoint, new SetVcpFrame(0x50, 0xF4, 0xD1)));
        Assert.Equal(DdcErrorKind.UnsupportedByTransport, ex.Kind);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Endpoint_from_another_transport_is_refused()
    {
        var a = new FakeTransport("a", raw: true);
        var b = new FakeTransport("b", raw: true);
        var endpoint = a.Add(TestData.Lg32Un880);

        Assert.Throws<ArgumentException>(() => b.Send(endpoint, new SetVcpFrame(0x51, 0x60, 0x11)));
    }
}
