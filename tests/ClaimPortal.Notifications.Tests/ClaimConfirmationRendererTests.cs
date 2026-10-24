using ClaimPortal.Contracts;
using ClaimPortal.Notifications.Templating;
using ClaimPortal.Notifications.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Shouldly;

namespace ClaimPortal.Notifications.Tests;

/// <summary>
/// Unit tests for the email content. No SMTP server, no broker, no database — which is the
/// reason templating is a separate seam from sending.
/// </summary>
public sealed class ClaimConfirmationRendererTests
{
    private static readonly NotificationContentOptions Content = new()
    {
        InsurerDisplayName = "Red River Mutual",
        SupportPhone = "1-800-370-2888",
        SupportEmail = "claims@redrivermutual.example",
        DisplayTimeZone = "America/Winnipeg",
    };

    private static ScribanClaimConfirmationRenderer CreateRenderer(
        NotificationContentOptions? content = null) =>
        new(
            new StaticOptionsMonitor<NotificationContentOptions>(content ?? Content),
            NullLogger<ScribanClaimConfirmationRenderer>.Instance);

    [Fact]
    public void PutsTheClaimNumberInTheSubjectAndBothBodies()
    {
        var claim = TestClaims.Submitted();

        var email = CreateRenderer().Render(claim);

        email.Subject.ShouldContain(claim.ClaimNumber);
        email.HtmlBody.ShouldContain(claim.ClaimNumber);
        email.TextBody.ShouldContain(claim.ClaimNumber);
    }

    [Fact]
    public void AddressesTheEmailToTheClaimant()
    {
        var claim = TestClaims.Submitted();

        var email = CreateRenderer().Render(claim);

        email.ToAddress.ShouldBe("dana.okonjo@example.ca");
        email.ToDisplayName.ShouldBe("Dana Okonjo");
    }

    [Fact]
    public void ProducesBothAlternativesSoAPlaintextClientStillSeesTheClaimNumber()
    {
        var email = CreateRenderer().Render(TestClaims.Submitted());

        email.HtmlBody.ShouldStartWith("<!DOCTYPE html>");
        email.HtmlBody.ShouldContain("</html>");

        // The plaintext part must be genuine plaintext, not the HTML repeated.
        email.TextBody.ShouldNotContain("<");
        email.TextBody.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IncludesTheClaimDetailsTheContractSaysTheEventCarries()
    {
        var claim = TestClaims.Submitted();

        var email = CreateRenderer().Render(claim);

        foreach (var body in new[] { email.HtmlBody, email.TextBody })
        {
            body.ShouldContain(claim.PolicyNumber);
            body.ShouldContain("Water damage");
            body.ShouldContain("October 24, 2026");
            body.ShouldContain(Content.SupportPhone);
            body.ShouldContain(Content.SupportEmail);
        }
    }

    [Fact]
    public void RendersTheSubmissionTimestampInTheConfiguredZoneRatherThanUtc()
    {
        // The event carries 14:03 UTC (contract §6); a policyholder in Manitoba should read
        // 9:03 AM, not do the arithmetic themselves.
        var email = CreateRenderer().Render(TestClaims.Submitted());

        email.TextBody.ShouldContain("October 26, 2026 at 9:03 a.m. CDT");
    }

    [Fact]
    public void FallsBackToUtcRatherThanFailingWhenTheHostHasNoSuchTimeZone()
    {
        // A slim container image with no tzdata must still send a correct email.
        var renderer = CreateRenderer(new NotificationContentOptions
        {
            InsurerDisplayName = Content.InsurerDisplayName,
            SupportPhone = Content.SupportPhone,
            SupportEmail = Content.SupportEmail,
            DisplayTimeZone = "Mars/Olympus_Mons",
        });

        var email = renderer.Render(TestClaims.Submitted());

        email.TextBody.ShouldContain("October 26, 2026 at 2:03 p.m. UTC");
    }

    [Fact]
    public void EscapesClaimantSuppliedTextInTheHtmlBody()
    {
        // Names arrive from a public form. The HTML part treats them as hostile text.
        var claim = TestClaims.Submitted(
            firstName: "<script>alert('xss')</script>",
            lastName: "O'Brien & Sons");

        var email = CreateRenderer().Render(claim);

        email.HtmlBody.ShouldNotContain("<script>");
        email.HtmlBody.ShouldContain("&lt;script&gt;");
        email.HtmlBody.ShouldContain("&amp;");
    }

    [Fact]
    public void KeepsTheSubjectOnASingleLineSoItCannotInjectAHeader()
    {
        var claim = TestClaims.Submitted(claimNumber: "CLM-20261026-AAAAAA");

        var subject = CreateRenderer().Render(claim).Subject;

        subject.ShouldNotContain("\n");
        subject.ShouldNotContain("\r");
        subject.ShouldBe("Red River Mutual claim CLM-20261026-AAAAAA received");
    }

    [Theory]
    [MemberData(nameof(EveryLossType))]
    public void HasCustomerFacingWordingForEveryLossTypeInTheContract(LossType lossType)
    {
        // The renderer throws on an unmapped loss type rather than printing the raw enum name.
        // This test is what makes that safe: adding a value to the frozen contract's §3 enum
        // without wording it fails the build here, not in a customer's inbox.
        var email = CreateRenderer().Render(TestClaims.Submitted(lossType: lossType));

        email.TextBody.ShouldNotBeNullOrWhiteSpace();
        email.HtmlBody.ShouldNotBeNullOrWhiteSpace();
    }

    public static TheoryData<LossType> EveryLossType() => new(Enum.GetValues<LossType>());

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{T}"/> so the renderer can be built with no DI
    /// container. The renderer takes a monitor so a configuration reload reaches it in
    /// production; the tests have nothing to reload.
    /// </summary>
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            internal static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
