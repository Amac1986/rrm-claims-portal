using Xunit;
using ClaimPortal.Domain.Claims;
using Shouldly;

namespace ClaimPortal.Domain.Tests;

public sealed class PostalCodeTests
{
    [Theory]
    [InlineData("K1A0B1", "K1A 0B1")]
    [InlineData("k1a 0b1", "K1A 0B1")]
    [InlineData(" k1a   0b1 ", "K1A 0B1")]
    [InlineData("K1A-0B1", null)]
    [InlineData("K1A0B", null)]
    [InlineData("K1A0B11", null)]
    [InlineData("K1A 0B1x", null)]
    [InlineData("1K1A0B", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryParse_normalizes_or_rejects(string? input, string? expected)
    {
        var parsed = PostalCode.TryParse(input, out var postalCode);

        if (expected is null)
        {
            parsed.ShouldBeFalse();
            return;
        }

        parsed.ShouldBeTrue();
        postalCode.Value.ShouldBe(expected);
    }

    [Fact]
    public void Parse_throws_for_an_invalid_postal_code()
    {
        Should.Throw<ArgumentException>(() => PostalCode.Parse("not a postal code"));
    }
}
