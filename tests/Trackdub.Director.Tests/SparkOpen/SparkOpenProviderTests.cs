using Trackdub.Director.Core.SparkOpen;

namespace Trackdub.Director.Tests.SparkOpen;

/// <summary>Provider selection is explicit and the default always stays hosted.</summary>
public sealed class SparkOpenProviderTests
{
    [Fact]
    public void Default_is_hosted()
    {
        Assert.Equal(TextProvider.Hosted, ProviderSelection.Default);
        Assert.Equal(TextProvider.Hosted, ProviderSelection.Select());
    }

    [Fact]
    public void Spark_open_is_opt_in_only()
    {
        Assert.Equal(TextProvider.Hosted, ProviderSelection.Select(useSparkOpen: false));
        Assert.Equal(TextProvider.SparkOpen, ProviderSelection.Select(useSparkOpen: true));
    }

    [Theory]
    [InlineData(null, TextProvider.Hosted)]
    [InlineData("", TextProvider.Hosted)]
    [InlineData("   ", TextProvider.Hosted)]
    [InlineData("hosted", TextProvider.Hosted)]
    [InlineData("HOSTED", TextProvider.Hosted)]
    [InlineData("spark-open", TextProvider.SparkOpen)]
    [InlineData("spark_open", TextProvider.SparkOpen)]
    [InlineData("SparkOpen", TextProvider.SparkOpen)]
    public void Parse_maps_names_and_defaults_to_hosted(string? name, TextProvider expected)
    {
        Assert.Equal(expected, ProviderSelection.Parse(name));
    }

    [Theory]
    [InlineData("baseten")]
    [InlineData("openai")]
    [InlineData("llama")]
    public void Parse_rejects_an_unknown_provider_rather_than_falling_through(string name)
    {
        // A typo must never silently select a provider the caller did not intend.
        Assert.Throws<SparkOpenConfigException>(() => ProviderSelection.Parse(name));
    }
}
