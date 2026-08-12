using ErpDoctor.Core;
using ErpDoctor.Infrastructure.WindowsEventDiagnostics;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class WindowsEventLogTests
{
    [Fact]
    public void Parse_ReadsProviderIdLevelTimestampAndMessage()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name=".NET Runtime" />
                <EventID>1026</EventID>
                <Level>2</Level>
                <TimeCreated SystemTime="2026-08-12T03:00:00.0000000Z" />
                <Computer>ERP-SRV01</Computer>
              </System>
              <RenderingInfo>
                <Message>Unhandled   exception in ERP API.</Message>
              </RenderingInfo>
            </Event>
            """;

        var parsed = WindowsEventXmlParser.Parse(xml);

        Assert.Equal(".NET Runtime", parsed.Provider);
        Assert.Equal(1026, parsed.EventId);
        Assert.Equal(2, parsed.Level);
        Assert.Equal("ERP-SRV01", parsed.Computer);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 12, 3, 0, 0, TimeSpan.Zero),
            parsed.TimeCreatedUtc);
        Assert.Equal("Unhandled exception in ERP API.", parsed.Message);
    }

    [Fact]
    public void Parse_FallsBackToEventDataWhenRenderedMessageIsUnavailable()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="IIS AspNetCore Module V2" />
                <EventID>1018</EventID>
                <Level>2</Level>
              </System>
              <EventData>
                <Data Name="ApplicationPath">D:\Apps\ERP</Data>
                <Data Name="ErrorCode">0x8007000d</Data>
              </EventData>
            </Event>
            """;

        var parsed = WindowsEventXmlParser.Parse(xml);

        Assert.Contains("ApplicationPath=D:\\Apps\\ERP", parsed.Message, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=0x8007000d", parsed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ErrorIsWarningAndCriticalEventIsCritical()
    {
        var query = new WindowsEventLogQueryOptions
        {
            Name = "Application errors",
            LogName = "Application"
        };
        var errorOnly = new[]
        {
            Event(level: 2, provider: ".NET Runtime", id: 1026)
        };
        var withCritical = new[]
        {
            Event(level: 2, provider: ".NET Runtime", id: 1026),
            Event(level: 1, provider: "Application Error", id: 1000)
        };

        var warning = WindowsEventLogEvaluator.Evaluate(query, errorOnly);
        var critical = WindowsEventLogEvaluator.Evaluate(query, withCritical);

        Assert.Equal(DiagnosticStatus.Warning, warning.Status);
        Assert.Equal(DiagnosticStatus.Critical, critical.Status);
        Assert.Contains("1 critical", critical.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ProviderFilterKeepsOnlyConfiguredProviders()
    {
        var query = new WindowsEventLogQueryOptions
        {
            Name = "ERP runtime errors",
            Providers = [".NET Runtime"]
        };
        var events = new[]
        {
            Event(level: 2, provider: ".NET Runtime", id: 1026),
            Event(level: 1, provider: "Unrelated Provider", id: 999)
        };

        var result = WindowsEventLogEvaluator.Evaluate(query, events);

        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal("1", result.EvidenceOrEmpty["eventCount"]);
        Assert.Contains(".NET Runtime", result.EvidenceOrEmpty["event1"], StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated Provider", result.EvidenceOrEmpty["event1"], StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RedactsCommonSecretsFromEventEvidence()
    {
        var query = new WindowsEventLogQueryOptions { Name = "Application errors" };
        var events = new[]
        {
            new WindowsEventSnapshot(
                DateTimeOffset.UtcNow,
                ".NET Runtime",
                1026,
                2,
                "ERP-SRV01",
                "Startup failed password=remove-me token=remove-too Authorization: Bearer remove-bearer")
        };

        var result = WindowsEventLogEvaluator.Evaluate(query, events);
        var rendered = result.EvidenceOrEmpty["event1"];

        Assert.DoesNotContain("remove-me", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("remove-too", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("remove-bearer", rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_NoEventsIsHealthy()
    {
        var query = new WindowsEventLogQueryOptions
        {
            Name = "Application errors",
            LookbackMinutes = 30
        };

        var result = WindowsEventLogEvaluator.Evaluate(
            query,
            Array.Empty<WindowsEventSnapshot>());

        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Contains("No matching recent events", result.Summary, StringComparison.Ordinal);
    }

    private static WindowsEventSnapshot Event(int level, string provider, int id) =>
        new(
            new DateTimeOffset(2026, 8, 12, 3, 0, 0, TimeSpan.Zero),
            provider,
            id,
            level,
            "ERP-SRV01",
            "Example event");
}
