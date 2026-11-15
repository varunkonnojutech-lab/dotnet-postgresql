using PostgresDemo.Services;

namespace PostgresDemo.Tests;

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData("wireless headphones", "wireless:* & headphones:*")]
    [InlineData("wireless", "wireless:*")]
    [InlineData("  USB-C   dock  ", "usb:* & c:* & dock:*")]
    [InlineData("noise-cancelling wireless", "noise:* & cancelling:* & wireless:*")]
    [InlineData("C++: the (good) parts", "c:* & the:* & good:* & parts:*")]
    [InlineData("don't stop", "dont:* & stop:*")]
    public void ToTsQuery_BuildsPrefixQuery(string input, string expected)
    {
        Assert.Equal(expected, SearchQuery.ToTsQuery(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void ToTsQuery_ReturnsEmpty_WhenNothingSearchable(string? input)
    {
        Assert.Equal(string.Empty, SearchQuery.ToTsQuery(input));
    }

    [Theory]
    [InlineData("USB-C", "usb-c")]
    [InlineData("Noise Cancelling", "noise-cancelling")]
    [InlineData("  4K  ", "4k")]
    public void NormalizeTag_LowercasesAndHyphenates(string input, string expected)
    {
        Assert.Equal(expected, SearchQuery.NormalizeTag(input));
    }

    [Fact]
    public void SanitizeTerm_DropsTsqueryOperators()
    {
        // A user typing tsquery operators must not change the query's meaning.
        Assert.Equal("ab", SearchQuery.SanitizeTerm("a&b"));
        Assert.Equal("ab", SearchQuery.SanitizeTerm("a|b"));
        Assert.Equal("ab", SearchQuery.SanitizeTerm("a!b"));
        Assert.Equal("ab", SearchQuery.SanitizeTerm("a:b"));
        Assert.Equal("ab", SearchQuery.SanitizeTerm("(a)b*"));
    }
}
