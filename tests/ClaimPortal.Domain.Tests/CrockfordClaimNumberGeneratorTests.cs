using Xunit;
using ClaimPortal.Domain.Claims;
using Shouldly;

namespace ClaimPortal.Domain.Tests;

public sealed class CrockfordClaimNumberGeneratorTests
{
    [Fact]
    public void Generate_uses_the_utc_submission_date()
    {
        // 2026-10-26 23:30 in UTC-05:00 is already the 27th in UTC.
        var submittedAt = new DateTimeOffset(2026, 10, 26, 23, 30, 0, TimeSpan.FromHours(-5));

        var number = new CrockfordClaimNumberGenerator().Generate(submittedAt);

        number.Value.ShouldStartWith("CLM-20261027-");
    }

    [Fact]
    public void Generate_produces_a_six_character_crockford_suffix()
    {
        var number = new CrockfordClaimNumberGenerator()
            .Generate(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));

        var suffix = number.Value[13..];

        suffix.Length.ShouldBe(ClaimNumber.SuffixLength);
        suffix.ShouldAllBe(c => ClaimNumber.Alphabet.Contains(c));
    }

    [Fact]
    public void Generate_is_not_deterministic()
    {
        var generator = new CrockfordClaimNumberGenerator();
        var submittedAt = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        var numbers = Enumerable.Range(0, 50)
            .Select(_ => generator.Generate(submittedAt).Value)
            .ToHashSet(StringComparer.Ordinal);

        numbers.Count.ShouldBeGreaterThan(1);
    }
}
