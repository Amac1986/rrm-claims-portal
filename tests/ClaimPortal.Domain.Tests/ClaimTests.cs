using Xunit;
using ClaimPortal.Domain.Claims;
using Shouldly;

namespace ClaimPortal.Domain.Tests;

public sealed class ClaimTests
{
    private static readonly DateTimeOffset SubmittedAt =
        new(2026, 10, 26, 14, 3, 11, TimeSpan.Zero);

    [Fact]
    public void Create_trims_text_and_starts_in_the_received_state()
    {
        var claim = Create();

        claim.PolicyNumber.ShouldBe("HO-4471902");
        claim.Description.ShouldBe("The basement flooded after a pipe burst.");
        claim.Claimant.FirstName.ShouldBe("Dana");
        claim.SubmittedAt.ShouldBe(SubmittedAt);
        claim.Status.ShouldBe(ClaimStatus.Received);
    }

    [Fact]
    public void Create_rejects_a_negative_estimate()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(estimatedLossAmount: -1m));
    }

    [Fact]
    public void Create_rejects_blank_required_text()
    {
        Should.Throw<ArgumentException>(() => Create(policyNumber: " "));
        Should.Throw<ArgumentException>(() => Create(description: ""));
    }

    [Fact]
    public void ReissueClaimNumber_keeps_the_claim_identity()
    {
        var claim = Create();
        var originalId = claim.Id;
        var replacement = ClaimNumber.FromParts(new DateOnly(2026, 10, 26), "ABCDEF");

        claim.ReissueClaimNumber(replacement);

        claim.Id.ShouldBe(originalId);
        claim.ClaimNumber.Value.ShouldBe(replacement.Value);
    }

    [Fact]
    public void Create_uses_a_time_ordered_uuidv7()
    {
        var earlier = Create(submittedAt: SubmittedAt);
        var later = Create(submittedAt: SubmittedAt.AddMinutes(1));

        later.Id.CompareTo(earlier.Id).ShouldBeGreaterThan(0);
    }

    private static Claim Create(
        string policyNumber = "HO-4471902",
        string description = "  The basement flooded after a pipe burst.  ",
        decimal? estimatedLossAmount = 12500m,
        DateTimeOffset? submittedAt = null)
        => Claim.Create(
            ClaimNumber.FromParts(new DateOnly(2026, 10, 26), "K7F3QX"),
            policyNumber,
            new Claimant("  Dana ", " Okonjo ", "dana.okonjo@example.ca", "204-555-0100"),
            new DateOnly(2026, 10, 24),
            LossType.Water,
            description,
            estimatedLossAmount,
            new PropertyAddress(
                "12 Main Street",
                null,
                "Winnipeg",
                Province.MB,
                PostalCode.Parse("R3C 1A1")),
            submittedAt ?? SubmittedAt);
}
