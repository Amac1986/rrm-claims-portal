using ClaimPortal.Domain.Claims;
using Shouldly;
using Xunit;

namespace ClaimPortal.Domain.Tests;

public sealed class BoundedClaimNumberRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_returns_the_first_successful_attempt()
    {
        var policy = new BoundedClaimNumberRetryPolicy();

        var attempts = 0;
        var result = await policy.ExecuteAsync(
            (attempt, _) =>
            {
                attempts = attempt;
                return Task.FromResult(42);
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(42);
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_retries_a_collision_and_then_succeeds()
    {
        var policy = new BoundedClaimNumberRetryPolicy(maxAttempts: 3);
        var attempts = 0;

        var result = await policy.ExecuteAsync(
            (attempt, _) =>
            {
                attempts = attempt;
                if (attempt < 3)
                {
                    throw new ClaimNumberCollisionException(Number());
                }

                return Task.FromResult("ok");
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe("ok");
        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task ExecuteAsync_rethrows_after_the_budget_is_exhausted()
    {
        var policy = new BoundedClaimNumberRetryPolicy(maxAttempts: 3);
        var attempts = 0;

        await Should.ThrowAsync<ClaimNumberCollisionException>(() =>
            policy.ExecuteAsync<int>(
                (attempt, _) =>
                {
                    attempts = attempt;
                    throw new ClaimNumberCollisionException(Number());
                },
                TestContext.Current.CancellationToken));

        attempts.ShouldBe(3);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_budget()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BoundedClaimNumberRetryPolicy(0));
    }

    private static ClaimNumber Number() => ClaimNumber.FromParts(new DateOnly(2026, 10, 26), "K7F3QX");
}
