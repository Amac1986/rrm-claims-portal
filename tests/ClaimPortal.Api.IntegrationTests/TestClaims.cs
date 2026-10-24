using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimPortal.Contracts.Api;
using ClaimPortal.Domain.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ClaimPortal.Api.IntegrationTests;

internal static class TestClaims
{
    /// <summary>
    /// The instant the scripted-clock tests run at. Deliberately relative to wall-clock now
    /// rather than a hard-coded date: the validator's incident-date window is 365 days, and
    /// a fixed date would make this suite fail a year from now for no real reason.
    /// </summary>
    public static readonly DateTimeOffset SubmissionTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// The contract serialises enums as PascalCase strings, and the API's validator expects
    /// that spelling. Posting with the default web options would send a number instead and
    /// quietly test a different path.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static SubmitClaimRequest ValidRequest(string policyNumber = "HO-4471902") => new()
    {
        PolicyNumber = policyNumber,
        FirstName = "Dana",
        LastName = "Okonjo",
        Email = "dana.okonjo@example.ca",
        Phone = "204-555-0100",
        IncidentDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3),
        LossType = ClaimPortal.Contracts.LossType.Water,
        Description = "The basement flooded after a pipe burst.",
        EstimatedLossAmount = 12500m,
        PropertyAddress = new ClaimPortal.Contracts.Api.PropertyAddress
        {
            AddressLine1 = "12 Main Street",
            City = "Winnipeg",
            Province = "MB",
            PostalCode = "R3C1A1",
        },
    };

    /// <summary>
    /// Replaces the clock and the generator so a test can force a database collision at a
    /// known claim number. The API itself, including the retry policy and the outbox, stays
    /// untouched.
    /// </summary>
    public static Action<IServiceCollection> WithClaimNumbers(params ClaimNumber[] numbers)
    {
        var scripted = new ScriptedClaimNumberGenerator(numbers);

        return services =>
        {
            services.RemoveAll<IClaimNumberGenerator>();
            services.AddSingleton<IClaimNumberGenerator>(scripted);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(SubmissionTime));
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class ScriptedClaimNumberGenerator(params ClaimNumber[] numbers) : IClaimNumberGenerator
    {
        private readonly Queue<ClaimNumber> _numbers = new(numbers);

        public ClaimNumber Generate(DateTimeOffset submittedAt) =>
            _numbers.TryDequeue(out var claimNumber)
                ? claimNumber
                : throw new InvalidOperationException(
                    "The test scripted fewer claim numbers than the API asked for.");
    }
}
