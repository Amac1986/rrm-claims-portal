using ClaimPortal.Contracts;
using ClaimPortal.Notifications.Consuming;
using ClaimPortal.Notifications.Email;
using ClaimPortal.Notifications.Idempotency;
using ClaimPortal.Notifications.Tests.Support;
using MassTransit;
using MassTransit.Testing;
using Shouldly;
using Xunit;

namespace ClaimPortal.Notifications.Tests;

/// <summary>
/// The behaviours the assignment asks to be demonstrated: consumed, faulted, retried and
/// deduplicated, plus the dead-letter destination and the exactly-one-email guarantee.
/// </summary>
public sealed class ClaimSubmittedConsumerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConsumesClaimSubmittedAndSendsOneEmailContainingTheClaimNumber()
    {
        await using var harness = await NotificationWorkerHarness.StartAsync(cancellationToken: Ct);
        var claim = TestClaims.Submitted();
        var messageId = Guid.NewGuid();

        await harness.PublishAsync(claim, messageId);

        (await harness.Harness.Consumed.Any<ClaimSubmitted>(Ct)).ShouldBeTrue();

        var consumer = harness.Harness.GetConsumerHarness<ClaimSubmittedConsumer>();
        (await consumer.Consumed.Any<ClaimSubmitted>(Ct)).ShouldBeTrue();

        harness.Sender.Attempts.ShouldBe(1);

        var email = harness.Sender.SingleAccepted;
        email.ToAddress.ShouldBe(claim.ClaimantEmail);

        // The one thing the email must carry, per the brief. Asserted on every part of it.
        email.Subject.ShouldContain(claim.ClaimNumber);
        email.HtmlBody.ShouldContain(claim.ClaimNumber);
        email.TextBody.ShouldContain(claim.ClaimNumber);

        // And the delivery is recorded, which is what makes the next redelivery a no-op.
        var delivery = await harness.GetDeliveryAsync(messageId);
        delivery.ShouldNotBeNull();
        delivery.Status.ShouldBe(NotificationDeliveryStatus.Sent);
        delivery.AttemptCount.ShouldBe(1);
        delivery.SentAt.ShouldNotBeNull();
        delivery.ClaimNumber.ShouldBe(claim.ClaimNumber);
    }

    [Fact]
    public async Task RetriesATransientSmtpFailureAndStillSendsExactlyOneEmail()
    {
        // Fails twice, succeeds on the third attempt — inside the retry limit of 3.
        await using var harness = await NotificationWorkerHarness.StartAsync(
            RecordingEmailSender.FailTransientlyTimes(2),
            cancellationToken: Ct);

        var claim = TestClaims.Submitted();
        var messageId = Guid.NewGuid();

        await harness.PublishAsync(claim, messageId);
        await harness.WaitForAcceptedEmailsAsync(1, Ct);

        // Three calls to the sender, one email out: the retry policy did its job, and the
        // idempotency store did not block the retries, because a reported failure releases the
        // claim rather than leaving it owned.
        harness.Sender.Attempts.ShouldBe(3);
        harness.Sender.Accepted.Count.ShouldBe(1);

        // The message succeeded, so it must not have faulted or dead-lettered.
        (await harness.Harness.Published.Any<Fault<ClaimSubmitted>>(Ct)).ShouldBeFalse();
        harness.DeadLettered.ShouldBeEmpty();

        var delivery = await harness.GetDeliveryAsync(messageId);
        delivery.ShouldNotBeNull();
        delivery.Status.ShouldBe(NotificationDeliveryStatus.Sent);

        // Three attempts recorded against the row, so an operator can see it was not clean.
        delivery.AttemptCount.ShouldBe(3);
    }

    [Fact]
    public async Task FaultsAndDeadLettersWhenEveryRetryIsExhausted()
    {
        await using var harness = await NotificationWorkerHarness.StartAsync(
            RecordingEmailSender.AlwaysFailTransiently(),
            cancellationToken: Ct);

        var claim = TestClaims.Submitted();
        var messageId = Guid.NewGuid();

        await harness.PublishAsync(claim, messageId);

        (await harness.Harness.Published.Any<Fault<ClaimSubmitted>>(Ct)).ShouldBeTrue();

        // The retry limit is 3, so the first attempt plus three retries is four calls.
        harness.Sender.Attempts.ShouldBe(4);
        harness.Sender.Accepted.ShouldBeEmpty();

        // The message is on the endpoint's _error queue — MassTransit's dead-letter
        // destination — and the failure reason is on the row for support to read.
        await harness.WaitForDeadLetterAsync(claim.ClaimId, Ct);

        var delivery = await harness.GetDeliveryAsync(messageId);
        delivery.ShouldNotBeNull();
        delivery.Status.ShouldBe(NotificationDeliveryStatus.Failed);
        delivery.SentAt.ShouldBeNull();
        delivery.LastError.ShouldNotBeNull();
        delivery.LastError.ShouldContain(nameof(TransientEmailDeliveryException));
    }

    [Fact]
    public async Task DeadLettersAPermanentRejectionWithoutBurningTheRetryBudget()
    {
        await using var harness = await NotificationWorkerHarness.StartAsync(
            RecordingEmailSender.AlwaysFailPermanently(),
            cancellationToken: Ct);

        var claim = TestClaims.Submitted();
        var messageId = Guid.NewGuid();

        await harness.PublishAsync(claim, messageId);

        (await harness.Harness.Published.Any<Fault<ClaimSubmitted>>(Ct)).ShouldBeTrue();
        await harness.WaitForDeadLetterAsync(claim.ClaimId, Ct);

        // Exactly one attempt. An SMTP 5xx will be refused identically every time, so the
        // retry policy deliberately excludes PermanentEmailDeliveryException: the message
        // reaches a human on the error queue in milliseconds instead of twenty minutes.
        harness.Sender.Attempts.ShouldBe(1);
        harness.Sender.Accepted.ShouldBeEmpty();

        var delivery = await harness.GetDeliveryAsync(messageId);
        delivery.ShouldNotBeNull();
        delivery.AttemptCount.ShouldBe(1);
        delivery.Status.ShouldBe(NotificationDeliveryStatus.Failed);
    }

    [Fact]
    public async Task ReturnsTheMessageToTheBrokerOnADelayOnceTheInProcessRetriesAreSpent()
    {
        // Proves UseDelayedRedelivery is actually in the pipeline, not merely configured.
        // One in-process retry (so two sends per delivery) and one redelivery interval, which
        // makes the expected total deterministic: 2 sends, redelivered, 2 more sends, _error.
        await using var harness = await NotificationWorkerHarness.StartAsync(
            RecordingEmailSender.AlwaysFailTransiently(),
            resilience =>
            {
                resilience.RetryLimit = 1;
                resilience.RedeliveryIntervals = [TimeSpan.FromMilliseconds(100)];
            },
            cancellationToken: Ct);

        var claim = TestClaims.Submitted();
        await harness.PublishAsync(claim, Guid.NewGuid());

        await harness.WaitForDeadLetterAsync(claim.ClaimId, Ct);

        harness.Sender.Attempts.ShouldBe(4);
        harness.Sender.Accepted.ShouldBeEmpty();
    }

    [Fact]
    public async Task RedeliveryAfterASuccessfulSendDoesNotSendASecondEmail()
    {
        await using var harness = await NotificationWorkerHarness.StartAsync(cancellationToken: Ct);
        var claim = TestClaims.Submitted();

        // The same MessageId twice. Per contract §2 the MessageId is the outbox row id and is
        // stable across redeliveries, so this is exactly what the consumer sees when a worker
        // is killed after SMTP accepted the mail but before the message was acked.
        var messageId = Guid.NewGuid();

        await harness.PublishAsync(claim, messageId);
        await harness.WaitForAcceptedEmailsAsync(1, Ct);
        harness.Sender.Attempts.ShouldBe(1);

        await harness.PublishAsync(claim, messageId);
        await harness.WaitForClaimAttemptsAsync(2, Ct);

        // The redelivery reached the consumer and was told the email had already gone. This is
        // the assertion the whole idempotency layer exists for.
        var outcomes = harness.Claims.Claims;
        outcomes.Count.ShouldBe(2);
        outcomes[0].Outcome.ShouldBe(DeliveryClaimOutcome.Claimed);
        outcomes[1].Outcome.ShouldBe(DeliveryClaimOutcome.AlreadySent);
        outcomes[1].MaySend.ShouldBeFalse();

        // Exactly one email, and the redelivery acked rather than faulting.
        harness.Sender.Attempts.ShouldBe(1);
        harness.Sender.Accepted.Count.ShouldBe(1);
        (await harness.Harness.Published.Any<Fault<ClaimSubmitted>>(Ct)).ShouldBeFalse();
        harness.DeadLettered.ShouldBeEmpty();

        // One row, still recording a single attempt: the redelivery never took the claim.
        var delivery = await harness.GetDeliveryAsync(messageId);
        delivery.ShouldNotBeNull();
        delivery.Status.ShouldBe(NotificationDeliveryStatus.Sent);
        delivery.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task TwoDistinctMessageIdsForTheSameClaimEachSendAnEmail()
    {
        // The guard against over-deduplicating: the key is the message id, not the claim. If
        // the API ever legitimately re-publishes a claim through a new outbox row, the customer
        // gets the email. Deriving the key from the payload — which the contract explicitly
        // forbids — would silently swallow the second one.
        await using var harness = await NotificationWorkerHarness.StartAsync(cancellationToken: Ct);
        var claim = TestClaims.Submitted();

        await harness.PublishAsync(claim, Guid.NewGuid());
        await harness.WaitForAcceptedEmailsAsync(1, Ct);

        await harness.PublishAsync(claim, Guid.NewGuid());
        await harness.WaitForAcceptedEmailsAsync(2, Ct);

        harness.Sender.Accepted.Count.ShouldBe(2);
        harness.Claims.Claims.ShouldAllBe(claim => claim.Outcome == DeliveryClaimOutcome.Claimed);
    }

    [Fact]
    public async Task DeadLettersAMessageThatArrivesWithNoMessageId()
    {
        await using var harness = await NotificationWorkerHarness.StartAsync(cancellationToken: Ct);
        var claim = TestClaims.Submitted();

        // No MessageId means no idempotency key, so there is no safe way to send: a redelivery
        // would be indistinguishable from a new claim. Refusing loudly is the only honest
        // option, and it is a publisher bug that belongs where somebody will see it.
        await harness.PublishWithoutMessageIdAsync(claim);

        (await harness.Harness.Published.Any<Fault<ClaimSubmitted>>(Ct)).ShouldBeTrue();
        await harness.WaitForDeadLetterAsync(claim.ClaimId, Ct);

        harness.Sender.Attempts.ShouldBe(0);
    }
}
