namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Runs a claim submission, retrying a bounded number of times when the database rejects
/// the generated claim number as a duplicate.
/// </summary>
public interface IClaimNumberRetryPolicy
{
    /// <summary>Maximum number of attempts, including the first.</summary>
    int MaxAttempts { get; }

    /// <summary>
    /// Invokes <paramref name="attempt"/> until it succeeds or the attempt budget runs
    /// out, swallowing <see cref="ClaimNumberCollisionException"/> in between.
    /// </summary>
    /// <param name="attempt">
    /// The whole unit of work, including generating the claim number. It receives the
    /// 1-based attempt number.
    /// <para>
    /// <strong>Each invocation must run against a fresh unit of work</strong> — a new DI
    /// scope and therefore a new <c>DbContext</c>. Retrying inside one context would
    /// leave the previous attempt's outbox message still tracked as Added, and the
    /// successful attempt would then commit two <c>ClaimSubmitted</c> rows: one carrying
    /// the claim number the database just rejected.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ClaimNumberCollisionException">Every attempt collided.</exception>
    Task<TResult> ExecuteAsync<TResult>(
        Func<int, CancellationToken, Task<TResult>> attempt,
        CancellationToken cancellationToken = default);
}
