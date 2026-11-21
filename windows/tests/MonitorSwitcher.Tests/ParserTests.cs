using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Tests;

public class CapabilitiesParserTests
{
    [Fact]
    public void Lg_32un880_string_without_model_keyword_still_yields_the_model()
    {
        var caps = CapabilitiesParser.Parse(TestData.Lg32Un880Capabilities);

        Assert.Equal("UN880", caps.Model);
        Assert.Equal("2.1", caps.MccsVersion);
        Assert.Equal(new ushort[] { 0x11, 0x12, 0x0F, 0x00 }, caps.ValuesFor(0x60));
        Assert.Equal(new ushort[] { 0x05, 0x08, 0x0B }, caps.ValuesFor(0x14));
        Assert.True(caps.SupportsVcp(0xF4));
        Assert.True(caps.SupportsVcp(0xFF));
        Assert.False(caps.SupportsVcp(0x61));
    }

    [Fact]
    public void Well_formed_string_is_parsed()
    {
        var caps = CapabilitiesParser.Parse("(prot(monitor)type(LCD)model(U2720Q)cmds(01 02 03 07 0C E3 F3)vcp(02 04 10 12 60(0F 11 1B) D6(01 04 05))mccs_ver(2.1))");

        Assert.Equal("U2720Q", caps.Model);
        Assert.Equal(new ushort[] { 0x0F, 0x11, 0x1B }, caps.ValuesFor(0x60));
        Assert.Empty(caps.ValuesFor(0x10));
        Assert.True(caps.SupportsVcp(0x10));
    }

    [Fact]
    public void Missing_outer_parentheses_and_trailing_nul_are_tolerated()
    {
        var caps = CapabilitiesParser.Parse("model(XYZ)vcp(60(11 12))\0");
        Assert.Equal("XYZ", caps.Model);
        Assert.Equal(new ushort[] { 0x11, 0x12 }, caps.ValuesFor(0x60));
    }

    [Fact]
    public void Garbage_does_not_throw()
    {
        var caps = CapabilitiesParser.Parse("((((vcp(60(11");
        Assert.NotNull(caps);
    }
}

public class EdidParserTests
{
    /// <summary>Builds a minimal valid EDID base block.</summary>
    internal static byte[] BuildEdid(ushort manufacturerRaw, ushort product, uint serial, string? name, string? serialText = null)
    {
        var e = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(e, 0);
        e[8] = (byte)(manufacturerRaw >> 8);
        e[9] = (byte)manufacturerRaw;
        e[10] = (byte)product;
        e[11] = (byte)(product >> 8);
        BitConverter.GetBytes(serial).CopyTo(e, 12);
        e[16] = 4;
        e[17] = 2023 - 1990;
        e[18] = 1;
        e[19] = 4;

        void Descriptor(int offset, byte tag, string text)
        {
            e[offset + 3] = tag;
            var bytes = System.Text.Encoding.ASCII.GetBytes(text.Length < 13 ? text + "\n" : text[..13]);
            for (int i = 0; i < 13; i++)
            {
                e[offset + 5 + i] = i < bytes.Length ? bytes[i] : (byte)0x20;
            }
        }

        e[54] = 0x01; // first descriptor is a detailed timing
        if (name is not null)
        {
            Descriptor(72, 0xFC, name);
        }
        if (serialText is not null)
        {
            Descriptor(90, 0xFF, serialText);
        }

        byte sum = 0;
        for (int i = 0; i < 127; i++)
        {
            sum += e[i];
        }
        e[127] = (byte)(256 - sum);
        return e;
    }

    [Fact]
    public void Decodes_lg_32un880_identity()
    {
        var edid = BuildEdid(0x1E6D, 0x774F, 384592, "LG HDR 4K");

        Assert.True(EdidParser.TryParse(edid, out var info));
        Assert.Equal("GSM", info!.ManufacturerId);
        Assert.Equal(0x774F, info.ProductCode);
        Assert.Equal(384592u, info.SerialNumber);
        Assert.Equal("LG HDR 4K", info.MonitorName);
        Assert.Equal(2023, info.ManufactureYear);
        Assert.True(info.ChecksumValid);
        Assert.Equal("GSM-774F-0005DE50", MonitorIdentity.FromEdid(info).StableId);
    }

    [Fact]
    public void Serial_string_is_used_when_the_numeric_serial_is_zero()
    {
        var edid = BuildEdid(0x10AC, 0xA0F1, 0, "DELL U2720Q", "ABC-123");

        Assert.True(EdidParser.TryParse(edid, out var info));
        Assert.Equal("DEL", info!.ManufacturerId);
        Assert.Equal("ABC-123", info.SerialString);
        Assert.Equal("DEL-A0F1-ABC123", MonitorIdentity.FromEdid(info).StableId);
    }

    [Fact]
    public void Rejects_data_without_the_edid_header()
    {
        var edid = BuildEdid(0x1E6D, 0x774F, 1, "X");
        edid[0] = 0x01;
        Assert.False(EdidParser.TryParse(edid, out _));
        Assert.False(EdidParser.TryParse(new byte[10], out _));
    }

    [Theory]
    [InlineData(0x1E6D, "GSM")]
    [InlineData(0x10AC, "DEL")]
    [InlineData(0x4C2D, "SAM")]
    [InlineData(0x0610, "APP")]
    public void Decodes_pnp_manufacturer_ids(int raw, string expected) =>
        Assert.Equal(expected, EdidParser.DecodeManufacturerId((ushort)raw));
}
