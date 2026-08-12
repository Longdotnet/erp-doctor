using ErpDoctor.Plugin.Redis;
using Xunit;

namespace ErpDoctor.Tests;

public sealed class RedisCliTests
{
    [Fact]
    public void Classifier_DoesNotTreatInfoLoadingFieldAsError()
    {
        const string info = """
            # Persistence
            loading:0
            rdb_last_bgsave_status:ok
            """;

        var failure = RedisCli.ClassifyRedisFailure(info, string.Empty);

        Assert.Null(failure);
    }

    [Theory]
    [InlineData("NOPERM this account has no permissions", "ACL")]
    [InlineData("NOAUTH Authentication required.", "authentication")]
    [InlineData("WRONGPASS invalid username-password pair", "authentication")]
    [InlineData("LOADING Redis is loading the dataset in memory", "loading")]
    [InlineData("MISCONF Redis is configured to save", "configuration")]
    [InlineData("ERR unknown command", "error response")]
    public void Classifier_MapsKnownErrorPrefixesWithoutReturningRawText(
        string rawError,
        string expectedSummaryFragment)
    {
        var failure = RedisCli.ClassifyRedisFailure(rawError, string.Empty);

        Assert.NotNull(failure);
        Assert.Contains(expectedSummaryFragment, failure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawError, failure, StringComparison.Ordinal);
    }
}
