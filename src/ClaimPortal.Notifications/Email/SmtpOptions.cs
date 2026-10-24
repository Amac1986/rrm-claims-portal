using System.ComponentModel.DataAnnotations;
using MailKit.Security;

namespace ClaimPortal.Notifications.Email;

/// <summary>
/// SMTP connection settings. Every value is configuration-driven — there is no hard-coded
/// host, port or credential anywhere in this assembly.
/// </summary>
/// <remarks>
/// <see cref="Password"/> is never written to <c>appsettings*.json</c>. It arrives as the
/// <c>Smtp__Password</c> environment variable, sourced from a container or pipeline secret.
/// The local Mailpit catcher needs no credentials at all, which is why both
/// <see cref="UserName"/> and <see cref="Password"/> are optional.
/// </remarks>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 1025;

    /// <summary>
    /// TLS negotiation mode. <see cref="SecureSocketOptions.None"/> for the local Mailpit
    /// container; <see cref="SecureSocketOptions.StartTls"/> for every real relay.
    /// </summary>
    public SecureSocketOptions SecureSocketOptions { get; set; } = SecureSocketOptions.Auto;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    public string FromAddress { get; set; } = "claims@redrivermutual.example";

    [Required(AllowEmptyStrings = false)]
    public string FromDisplayName { get; set; } = "Red River Mutual Claims";

    /// <summary>
    /// Socket timeout. Kept well under the retry backoff so a hung relay surfaces as a
    /// transient failure rather than pinning a consumer thread.
    /// </summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}
