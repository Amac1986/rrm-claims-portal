using ClaimPortal.Contracts;

namespace ClaimPortal.Notifications.Tests.Support;

/// <summary>
/// The canonical <see cref="ClaimSubmitted"/> used across the suite. Mirrors the worked example
/// in docs/CONTRACT.md §2 so a reviewer can diff the test data against the frozen contract.
/// </summary>
internal static class TestClaims
{
    internal static ClaimSubmitted Submitted(
        string claimNumber = "CLM-20261026-K7F3QX",
        string firstName = "Dana",
        string lastName = "Okonjo",
        string email = "dana.okonjo@example.ca",
        LossType lossType = LossType.Water) =>
        new()
        {
            ClaimId = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            ClaimNumber = claimNumber,
            PolicyNumber = "HO-4471902",
            ClaimantFirstName = firstName,
            ClaimantLastName = lastName,
            ClaimantEmail = email,
            LossType = lossType,
            IncidentDate = new DateOnly(2026, 10, 24),

            // 14:03 UTC is 09:03 in Winnipeg on that date (CDT, UTC-5) — the renderer tests
            // assert the converted wording, so the offset here is load-bearing.
            SubmittedAt = new DateTimeOffset(2026, 10, 26, 14, 3, 11, 482, TimeSpan.Zero),
        };
}
