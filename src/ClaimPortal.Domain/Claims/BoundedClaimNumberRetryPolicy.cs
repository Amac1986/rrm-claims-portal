namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Default <see cref="IClaimNumberRetryPolicy"/>: a fixed, small attempt budget with no
/// backoff.
/// </summary>
/// <remarks>
/// <para>
/// No delay between attempts, on purpose. A claim-number collision is not contention and
/// not a transient fault — the previous suffix is simply taken, and the next one is drawn
/// from a billion-value keyspace independently of it. Waiting would add latency to a
/// request a customer is sitting in front of and would not improve the odds.
/// </para>
/// <para>
/// Three attempts. With roughly a 1-in-a-million chance that any single generated suffix
/// is already used on that date at a thousand claims a day, three independent attempts
/// put the odds of exhausting the budget around 1 in 10^18 — far below the rate at which
/// the database itself fails. Going higher only buys a longer stall before a 500 in the
/// case where something is genuinely wrong (a stuck clock, a broken RNG), and that case
/// is better surfaced loudly than retried quietly.
/// </para>
/// <para>
/// Rejected alternative: Polly. It is the right answer for transport-level transient
/// faults with jitter and circuit breaking; for "try a different random number up to
/// three times" it is a dependency carrying policy nobody will read.
/// </para>
/// </remarks>
public sealed class BoundedClaimNumberRetryPolicy : IClaimNumberRetryPolicy
{
    /// <summary>Default attempt budget.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>Creates the policy with an optional attempt budget.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAttempts"/> is less than one.</exception>
    public BoundedClaimNumberRetryPolicy(int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        MaxAttempts = maxAttempts;
    }

    /// <inheritdoc />
    public int MaxAttempts { get; }

    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<int, CancellationToken, Task<TResult>> attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        for (var attemptNumber = 1; ; attemptNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await attempt(attemptNumber, cancellationToken).ConfigureAwait(false);
            }
            catch (ClaimNumberCollisionException) when (attemptNumber < MaxAttempts)
            {
                // Exhausting the budget rethrows: an unexpectedly hot collision rate is a
                // defect in the generator or the clock, and it should reach the logs.
            }
        }
    }
}
