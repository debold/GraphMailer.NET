using GraphMailer.Service.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

/// <summary>Round-trip coverage for the SenderValidation config section.</summary>
public sealed class ConfigServiceSenderValidationTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _sut;

    public ConfigServiceSenderValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gm-cfg-sv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var protector = new EphemeralDataProtectionProvider().CreateProtector("GraphMailer.Config");
        _sut = new ConfigService(Path.Combine(_dir, "graphmailer.json"), protector);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Load_NoFile_SenderValidationDefaults()
    {
        var doc = _sut.Load();

        doc.SenderValidation.SvEnabled.Should().BeFalse();
        doc.SenderValidation.SvRefreshIntervalMinutes.Should().Be(60);
        doc.SenderValidation.SvFailClosed.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_SenderValidation_PreservesAllValues()
    {
        var doc = _sut.Load();
        doc.SenderValidation.SvEnabled = true;
        doc.SenderValidation.SvRefreshIntervalMinutes = 15;
        doc.SenderValidation.SvFailClosed = true;

        _sut.Save(doc);
        var reloaded = _sut.Load();

        reloaded.SenderValidation.SvEnabled.Should().BeTrue();
        reloaded.SenderValidation.SvRefreshIntervalMinutes.Should().Be(15);
        reloaded.SenderValidation.SvFailClosed.Should().BeTrue();
    }

    [Fact]
    public void Save_WritesSectionNameMatchingServiceOptions()
    {
        var doc = _sut.Load();
        doc.SenderValidation.SvEnabled = true;
        _sut.Save(doc);

        var json = File.ReadAllText(Path.Combine(_dir, "graphmailer.json"));
        json.Should().Contain("\"SenderValidation\"",
            "the section name must match SenderValidationOptions.SectionName so the service binds it");
        json.Should().Contain("\"RefreshIntervalMinutes\"");
    }
}
