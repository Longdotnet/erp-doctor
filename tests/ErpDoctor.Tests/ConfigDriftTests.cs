using ErpDoctor.Core;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class ConfigDriftTests
{
    [Fact]
    public void Compare_DetectsNestedDifferencesAndMissingValues()
    {
        const string left = """
            {
              "Environment": "Development",
              "Api": {
                "BaseUrl": "https://dev.example.test",
                "TimeoutSeconds": 30
              },
              "FeatureFlags": {
                "Beta": true
              }
            }
            """;
        const string right = """
            {
              "Environment": "Production",
              "Api": {
                "BaseUrl": "https://prod.example.test",
                "TimeoutSeconds": 30
              },
              "FeatureFlags": {
                "NewCheckout": true
              }
            }
            """;

        var report = JsonConfigDriftAnalyzer.Compare(left, right, "dev", "prod");

        Assert.Equal(4, report.Differences.Count);
        Assert.Contains(report.Differences, item =>
            item.Path == "Environment" && item.Kind == ConfigDriftKind.Different);
        Assert.Contains(report.Differences, item =>
            item.Path == "Api:BaseUrl" && item.Kind == ConfigDriftKind.Different);
        Assert.Contains(report.Differences, item =>
            item.Path == "FeatureFlags:Beta" && item.Kind == ConfigDriftKind.MissingRight);
        Assert.Contains(report.Differences, item =>
            item.Path == "FeatureFlags:NewCheckout" && item.Kind == ConfigDriftKind.MissingLeft);
        Assert.DoesNotContain(report.Differences, item => item.Path == "Api:TimeoutSeconds");
    }

    [Fact]
    public void Compare_NeverExposesSensitivePathValuesOrInlineTokens()
    {
        const string left = """
            {
              "ConnectionStrings": {
                "ERP": "Server=dev;Database=ERP;Password=left-password"
              },
              "Auth": {
                "ApiToken": "left-token"
              },
              "Api": {
                "Url": "https://example.test/health?token=left-inline&mode=full"
              }
            }
            """;
        const string right = """
            {
              "ConnectionStrings": {
                "ERP": "Server=prod;Database=ERP;Password=right-password"
              },
              "Auth": {
                "ApiToken": "right-token"
              },
              "Api": {
                "Url": "https://example.test/health?token=right-inline&mode=full"
              }
            }
            """;

        var report = JsonConfigDriftAnalyzer.Compare(left, right);
        var connection = Assert.Single(report.Differences, item => item.Path == "ConnectionStrings:ERP");
        var token = Assert.Single(report.Differences, item => item.Path == "Auth:ApiToken");
        var url = Assert.Single(report.Differences, item => item.Path == "Api:Url");

        Assert.True(connection.IsSensitive);
        Assert.Equal(JsonConfigDriftAnalyzer.RedactedValue, connection.LeftValue);
        Assert.Equal(JsonConfigDriftAnalyzer.RedactedValue, connection.RightValue);
        Assert.True(token.IsSensitive);
        Assert.Equal(JsonConfigDriftAnalyzer.RedactedValue, token.LeftValue);
        Assert.Equal(JsonConfigDriftAnalyzer.RedactedValue, token.RightValue);

        var rendered = string.Join(
            "\n",
            report.Differences.SelectMany(item => new[] { item.LeftValue, item.RightValue }));
        Assert.DoesNotContain("left-password", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("right-password", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("left-token", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("right-token", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("left-inline", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("right-inline", rendered, StringComparison.Ordinal);
        Assert.Contains("token=[REDACTED]", url.LeftValue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token=[REDACTED]", url.RightValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_IgnorePrefixRemovesWholeSubtree()
    {
        const string left = """
            {
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "Api": { "Timeout": 10 }
            }
            """;
        const string right = """
            {
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "Api": { "Timeout": 20 }
            }
            """;

        var report = JsonConfigDriftAnalyzer.Compare(
            left,
            right,
            ignorePrefixes: ["Logging"]);

        var difference = Assert.Single(report.Differences);
        Assert.Equal("Api:Timeout", difference.Path);
    }

    [Fact]
    public void Compare_TypeChangeSuppressesNoisyDescendantMissingEntries()
    {
        const string left = """
            {
              "Feature": {
                "Enabled": true,
                "Mode": "safe"
              }
            }
            """;
        const string right = """
            {
              "Feature": "disabled"
            }
            """;

        var report = JsonConfigDriftAnalyzer.Compare(left, right);

        var difference = Assert.Single(report.Differences);
        Assert.Equal("Feature", difference.Path);
        Assert.Equal(ConfigDriftKind.TypeChanged, difference.Kind);
    }

    [Fact]
    public void Compare_EquivalentConfigHasNoDrift()
    {
        const string left = """{ "Api": { "Timeout": 30 }, "Enabled": true }""";
        const string right = """{ "enabled": true, "api": { "timeout": 30 } }""";

        var report = JsonConfigDriftAnalyzer.Compare(left, right);

        Assert.Empty(report.Differences);
    }
}
