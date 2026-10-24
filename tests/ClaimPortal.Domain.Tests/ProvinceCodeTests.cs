using Xunit;
using ClaimPortal.Domain.Claims;
using Shouldly;

namespace ClaimPortal.Domain.Tests;

public sealed class ProvinceCodeTests
{
    [Theory]
    [InlineData("MB", Province.MB)]
    [InlineData("mb", Province.MB)]
    [InlineData("ON", Province.ON)]
    [InlineData(" yt ", Province.YT)]
    public void TryParse_accepts_the_thirteen_codes_case_insensitively(string input, Province expected)
    {
        ProvinceCode.TryParse(input, out var province).ShouldBeTrue();

        province.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("XX")]
    [InlineData("M")]
    [InlineData("MBB")]
    [InlineData("12")]
    public void TryParse_rejects_unknown_or_malformed_codes(string? input)
    {
        ProvinceCode.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void All_contains_exactly_the_contract_codes()
    {
        ProvinceCode.All.ShouldBe(
            ["AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT"],
            ignoreOrder: false);
    }
}
