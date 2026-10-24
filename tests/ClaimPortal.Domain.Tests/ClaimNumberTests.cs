using Xunit;
using ClaimPortal.Domain.Claims;
using Shouldly;

namespace ClaimPortal.Domain.Tests;

public sealed class ClaimNumberTests
{
    [Fact]
    public void FromParts_uses_the_contract_format()
    {
        var number = ClaimNumber.FromParts(new DateOnly(2026, 10, 26), "K7F3QX");

        number.Value.ShouldBe("CLM-20261026-K7F3QX");
        number.Value.Length.ShouldBe(ClaimNumber.TotalLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K7F3Q")]
    [InlineData("K7F3QXY")]
    [InlineData("K7F3QI")]
    [InlineData("K7F3QL")]
    [InlineData("K7F3QO")]
    [InlineData("K7F3QU")]
    [InlineData("k7f3qx")]
    [InlineData("K7F3Q-")]
    public void FromParts_rejects_suffixes_outside_the_crockford_alphabet(string suffix)
    {
        Should.Throw<ArgumentException>(() => ClaimNumber.FromParts(new DateOnly(2026, 10, 26), suffix));
    }

    [Fact]
    public void Alphabet_excludes_the_ambiguous_characters()
    {
        ClaimNumber.Alphabet.ShouldNotContain('I');
        ClaimNumber.Alphabet.ShouldNotContain('L');
        ClaimNumber.Alphabet.ShouldNotContain('O');
        ClaimNumber.Alphabet.ShouldNotContain('U');
        ClaimNumber.Alphabet.Length.ShouldBe(32);
    }

    [Fact]
    public void TryParse_round_trips_a_generated_number()
    {
        var original = ClaimNumber.FromParts(new DateOnly(2026, 10, 26), "K7F3QX");

        ClaimNumber.TryParse(original.Value, out var parsed).ShouldBeTrue();

        parsed.Value.ShouldBe(original.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CLM-20261026")]
    [InlineData("CLM-2026102-K7F3QX")]
    [InlineData("CLM-20261326-K7F3QX")]
    [InlineData("clm-20261026-K7F3QX")]
    [InlineData("CLM_20261026_K7F3QX")]
    [InlineData("CLM-20261026-k7f3qx")]
    public void TryParse_rejects_malformed_values(string? value)
    {
        ClaimNumber.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Parse_throws_for_a_malformed_value()
    {
        Should.Throw<ArgumentException>(() => ClaimNumber.Parse("nope"));
    }
}
