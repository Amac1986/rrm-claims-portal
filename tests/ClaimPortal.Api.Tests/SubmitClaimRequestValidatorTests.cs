using ClaimPortal.Api.Validation;
using ClaimPortal.Contracts.Api;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace ClaimPortal.Api.Tests;

public sealed class SubmitClaimRequestValidatorTests
{
    private static readonly FakeTimeProvider Clock =
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private readonly SubmitClaimRequestValidator _validator = new(Clock);

    [Fact]
    public async Task A_contract_valid_request_has_no_errors()
    {
        var result = await _validator.ValidateAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Errors_are_keyed_by_camel_case_field_names()
    {
        var request = ValidRequest() with
        {
            Email = "not-an-email",
            Description = "too short",
        };

        var result = await _validator.ValidateAsync(request, TestContext.Current.CancellationToken);

        result.IsValid.ShouldBeFalse();
        result.Errors.Select(failure => failure.PropertyName)
            .ShouldContain("email");
        result.Errors.Select(failure => failure.PropertyName)
            .ShouldContain("description");
    }

    [Fact]
    public async Task Nested_address_errors_use_dotted_camel_case_keys()
    {
        var request = ValidRequest() with
        {
            PropertyAddress = new PropertyAddress { AddressLine1 = "", PostalCode = "nope" },
        };

        var result = await _validator.ValidateAsync(request, TestContext.Current.CancellationToken);

        var keys = result.Errors.Select(failure => failure.PropertyName).ToArray();
        keys.ShouldContain("propertyAddress.addressLine1");
        keys.ShouldContain("propertyAddress.postalCode");
        keys.ShouldContain("propertyAddress.city");
        keys.ShouldContain("propertyAddress.province");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_000_001)]
    public async Task Estimated_loss_amount_is_bounded(decimal amount)
    {
        var result = await _validator.ValidateAsync(
            ValidRequest() with { EstimatedLossAmount = amount },
            TestContext.Current.CancellationToken);

        result.Errors.ShouldContain(failure => failure.PropertyName == "estimatedLossAmount");
    }

    [Fact]
    public async Task Incident_date_cannot_be_in_the_future()
    {
        var result = await _validator.ValidateAsync(
            ValidRequest() with { IncidentDate = new DateOnly(2026, 10, 25) },
            TestContext.Current.CancellationToken);

        result.Errors.ShouldContain(failure => failure.PropertyName == "incidentDate");
    }

    [Fact]
    public async Task Incident_date_cannot_be_more_than_365_days_ago()
    {
        var result = await _validator.ValidateAsync(
            ValidRequest() with { IncidentDate = new DateOnly(2025, 10, 23) },
            TestContext.Current.CancellationToken);

        result.Errors.ShouldContain(failure => failure.PropertyName == "incidentDate");
    }

    [Fact]
    public async Task Phone_rejects_letters()
    {
        var result = await _validator.ValidateAsync(
            ValidRequest() with { Phone = "call me maybe" },
            TestContext.Current.CancellationToken);

        result.Errors.ShouldContain(failure => failure.PropertyName == "phone");
    }

    private static SubmitClaimRequest ValidRequest() => new()
    {
        PolicyNumber = "HO-4471902",
        FirstName = "Dana",
        LastName = "Okonjo",
        Email = "dana.okonjo@example.ca",
        Phone = "204-555-0100",
        IncidentDate = new DateOnly(2026, 10, 20),
        LossType = ClaimPortal.Contracts.LossType.Water,
        Description = "The basement flooded after a pipe burst.",
        EstimatedLossAmount = 12500m,
        PropertyAddress = new PropertyAddress
        {
            AddressLine1 = "12 Main Street",
            City = "Winnipeg",
            Province = "MB",
            PostalCode = "R3C 1A1",
        },
    };
}
