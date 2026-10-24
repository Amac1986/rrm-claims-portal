using System.Diagnostics;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ClaimPortal.Notifications.Email;

/// <summary>
/// Sends a rendered email over SMTP with MailKit, as <c>multipart/alternative</c>.
/// </summary>
/// <remarks>
/// A fresh <see cref="SmtpClient"/> per send. MailKit's client is not thread-safe and holds a
/// stateful session; pooling it would mean synchronising access across concurrent consumers
/// for a connection setup that costs single-digit milliseconds against a local relay. The
/// honest reason to pool is a high-volume sender, and at that volume this class is replaced
/// by an HTTP provider behind <see cref="IEmailSender"/> anyway.
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<SmtpOptions> _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptionsMonitor<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.CurrentValue;
        var mime = BuildMimeMessage(message, options);

        using var client = new SmtpClient
        {
            Timeout = (int)TimeSpan.FromSeconds(options.TimeoutSeconds).TotalMilliseconds,
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(options.Host, options.Port, options.SecureSocketOptions, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                await client.AuthenticateAsync(options.UserName, options.Password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            var response = await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "SMTP accepted confirmation email for {Recipient} via {SmtpHost}:{SmtpPort} in {ElapsedMs}ms. Server said: {SmtpResponse}",
                message.ToAddress,
                options.Host,
                options.Port,
                stopwatch.ElapsedMilliseconds,
                response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a delivery failure. Let MassTransit redeliver the message.
            throw;
        }
        catch (Exception ex)
        {
            throw Classify(ex, options);
        }
    }

    /// <summary>
    /// Maps provider failures onto the retryable/terminal split the retry policy keys off.
    /// </summary>
    private static EmailDeliveryException Classify(Exception ex, SmtpOptions options)
    {
        var where = $"{options.Host}:{options.Port}";

        return ex switch
        {
            // A 5xx is a considered refusal — the relay will refuse the identical message again.
            SmtpCommandException smtp when (int)smtp.StatusCode >= 500 && (int)smtp.StatusCode < 600 =>
                new PermanentEmailDeliveryException(
                    $"{where} permanently rejected the message with SMTP {(int)smtp.StatusCode} ({smtp.ErrorCode}).", smtp),

            // 4xx is "not now" — mailbox busy, greylisted, out of storage.
            SmtpCommandException smtp =>
                new TransientEmailDeliveryException(
                    $"{where} temporarily rejected the message with SMTP {(int)smtp.StatusCode} ({smtp.ErrorCode}).", smtp),

            // Bad credentials are permanent for this deployment; retrying cannot fix them.
            AuthenticationException auth =>
                new PermanentEmailDeliveryException($"{where} rejected the configured SMTP credentials.", auth),

            // The relay demands authentication and this deployment has none configured. Also
            // permanent: it is a missing Smtp__UserName/Smtp__Password, not a bad moment.
            ServiceNotAuthenticatedException notAuthenticated =>
                new PermanentEmailDeliveryException(
                    $"{where} requires SMTP authentication but no credentials are configured.", notAuthenticated),

            // A failed handshake is usually misconfiguration, but can be a mid-rotation cert.
            SslHandshakeException ssl =>
                new TransientEmailDeliveryException($"TLS handshake with {where} failed.", ssl),

            SmtpProtocolException or SocketException or IOException or TimeoutException =>
                new TransientEmailDeliveryException($"{where} was unreachable or dropped the session.", ex),

            // Unknown failures are treated as transient on purpose: a retry costs one backoff
            // interval, whereas misclassifying a blip as permanent loses the customer's email.
            _ => new TransientEmailDeliveryException($"Unexpected failure sending via {where}.", ex),
        };
    }

    private static MimeMessage BuildMimeMessage(EmailMessage message, SmtpOptions options)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromDisplayName, options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToDisplayName, message.ToAddress));
        mime.Subject = message.Subject;

        // multipart/alternative, plaintext first: per RFC 2046 the *last* part is the one a
        // client prefers, so HTML must come second for HTML-capable clients to pick it.
        var body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
        };
        mime.Body = body.ToMessageBody();

        return mime;
    }
}
