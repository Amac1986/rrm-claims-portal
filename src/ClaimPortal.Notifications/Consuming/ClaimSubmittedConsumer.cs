using ClaimPortal.Contracts;
using ClaimPortal.Notifications.Email;
using ClaimPortal.Notifications.Idempotency;
using ClaimPortal.Notifications.Templating;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ClaimPortal.Notifications.Consuming;

/// <summary>
/// Reacts to <see cref="ClaimSubmitted"/> by sending the customer's confirmation email.
/// </summary>
/// <remarks>
/// <para>
/// This class is the point of the whole service: the API publishes an event and returns 201
/// without ever touching an SMTP socket, and the email is composed and delivered here, in a
/// separate process, off the request thread. A slow or dead mail relay cannot make claim
/// submission slow or fail — it makes <em>this</em> message retry.
/// </para>
/// <para>
/// The consumer itself is deliberately thin. Rendering lives behind
/// <see cref="IClaimConfirmationRenderer"/>, delivery behind <see cref="IEmailSender"/>, and
/// deduplication behind <see cref="INotificationDeliveryStore"/>; what is left here is the
/// ordering of those three calls, which is the part that has to be right.
/// </para>
/// </remarks>
public sealed class ClaimSubmittedConsumer : IConsumer<ClaimSubmitted>
{
    private readonly IClaimConfirmationRenderer _renderer;
    private readonly IEmailSender _sender;
    private readonly INotificationDeliveryStore _deliveries;
    private readonly ILogger<ClaimSubmittedConsumer> _logger;

    public ClaimSubmittedConsumer(
        IClaimConfirmationRenderer renderer,
        IEmailSender sender,
        INotificationDeliveryStore deliveries,
        ILogger<ClaimSubmittedConsumer> logger)
    {
        _renderer = renderer;
        _sender = sender;
        _deliveries = deliveries;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ClaimSubmitted> context)
    {
        var claim = context.Message;

        // Contract §2 makes MessageId the idempotency key and guarantees it is the outbox row
        // id, stable across redeliveries. Without it there is no way to tell a redelivery from
        // a new claim, so refuse the message permanently rather than risk a duplicate email:
        // this is a publisher bug and belongs in _error where somebody will see it.
        var messageId = context.MessageId
            ?? throw new PermanentEmailDeliveryException(
                $"ClaimSubmitted for claim {claim.ClaimNumber} arrived with no MessageId. The "
                + "transport MessageId is the idempotency key (contract §2); refusing to send "
                + "an email that could not be deduplicated.");

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = messageId,
            ["ClaimId"] = claim.ClaimId,
            ["ClaimNumber"] = claim.ClaimNumber,
        });

        var delivery = await _deliveries.TryClaimAsync(
            new DeliveryAttemptKey(messageId, claim.ClaimId, claim.ClaimNumber, claim.ClaimantEmail),
            context.CancellationToken);

        if (!delivery.MaySend)
        {
            // Returning normally acks the message. That is the correct response to both
            // outcomes: the email either already went out, or another attempt owns it and the
            // broker will redeliver that one if its process dies.
            LogSkipped(delivery, claim);
            return;
        }

        var email = Render(claim);

        try
        {
            await _sender.SendAsync(email, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The worker is shutting down; we do not know whether the relay accepted it. Leave
            // the row Pending and let the stale-claim window recover it, rather than recording
            // a failure we are not sure about.
            throw;
        }
        catch (Exception ex)
        {
            // Record the failure before rethrowing, so the retry can take the claim straight
            // back and an operator can see why from SQL. CancellationToken.None on purpose:
            // this write must land even if the host is being torn down, because a Pending row
            // is the one state that makes a later redelivery wait out the stale window.
            await _deliveries.MarkFailedAsync(messageId, Describe(ex), CancellationToken.None);

            _logger.LogWarning(
                ex,
                "Attempt {AttemptCount} to email the confirmation for claim {ClaimNumber} to {Recipient} failed.",
                delivery.AttemptCount,
                claim.ClaimNumber,
                email.ToAddress);

            // Rethrow so MassTransit's retry, delayed redelivery and finally _error apply.
            throw;
        }

        // SMTP has accepted the message. This write is what makes a crash before the ack safe.
        await _deliveries.MarkSentAsync(messageId, CancellationToken.None);

        _logger.LogInformation(
            "Sent the claim confirmation for {ClaimNumber} to {Recipient} on attempt {AttemptCount}.",
            claim.ClaimNumber,
            email.ToAddress,
            delivery.AttemptCount);
    }

    /// <summary>
    /// A render failure is permanent by construction: the renderer is a pure function of the
    /// event and the configuration, so the next attempt produces the identical exception.
    /// Translating it means the retry policy does not burn five backoff intervals discovering
    /// that, and the message reaches <c>_error</c> with the reason attached.
    /// </summary>
    private EmailMessage Render(ClaimSubmitted claim)
    {
        try
        {
            return _renderer.Render(claim);
        }
        catch (Exception ex) when (ex is not EmailDeliveryException)
        {
            throw new PermanentEmailDeliveryException(
                $"Could not render the confirmation email for claim {claim.ClaimNumber}.", ex);
        }
    }

    private void LogSkipped(DeliveryClaim delivery, ClaimSubmitted claim)
    {
        if (delivery.Outcome == DeliveryClaimOutcome.AlreadySent)
        {
            _logger.LogInformation(
                "Redelivery of the confirmation for claim {ClaimNumber}: already sent at {SentAt:O} after "
                + "{AttemptCount} attempt(s). No email sent.",
                claim.ClaimNumber,
                delivery.SentAt,
                delivery.AttemptCount);
            return;
        }

        _logger.LogWarning(
            "Another attempt currently owns the confirmation for claim {ClaimNumber}. No email sent.",
            claim.ClaimNumber);
    }

    /// <summary>
    /// What gets written to <c>last_error</c>. The message chain, not the stack trace: support
    /// staff read this column, and the stack trace is already in the logs and on the
    /// <c>_error</c> message's headers.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null && messages.Count < 4; current = current.InnerException)
        {
            messages.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", messages);
    }
}
