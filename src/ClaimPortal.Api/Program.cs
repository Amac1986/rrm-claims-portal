using System.Text.Json.Serialization;
using ClaimPortal.Api;
using ClaimPortal.Api.Messaging;
using ClaimPortal.Api.Serialization;
using ClaimPortal.Api.Validation;
using ClaimPortal.Domain.Claims;
using ClaimPortal.Infrastructure.Persistence;
using FluentValidation;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// The contract makes every enum a PascalCase string on the wire. Configure the HTTP
// serializer once, here, rather than sprinkling [JsonConverter] over frozen contract types.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
});

// Error keys are camelCase request field names in the contract; nested keys use dots.
ValidationConfiguration.Configure();

builder.Services.AddValidatorsFromAssemblyContaining<SubmitClaimRequestValidator>();
builder.Services.AddSingleton<IClaimNumberGenerator, CrockfordClaimNumberGenerator>();
builder.Services.AddSingleton<IClaimNumberRetryPolicy>(_ => new BoundedClaimNumberRetryPolicy());
builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("Claims")
    ?? "Host=localhost;Port=5432;Database=claimportal;Username=claimportal;Password=claimportal";

builder.Services.AddDbContext<ClaimsDbContext>(options =>
    ClaimsDbContextOptions.Configure(options, connectionString));
builder.Services.AddScoped<IClaimRepository, ClaimRepository>();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHostedService<ClaimsDatabaseMigrator>();

builder.Services.AddMassTransit(bus =>
{
    // The transactional outbox lives in the same DbContext as the claim row, so the claim and
    // its ClaimSubmitted message commit or roll back together. See docs/CONTRACT.md section 2.
    bus.AddEntityFrameworkOutbox<ClaimsDbContext>(outbox =>
    {
        outbox.UsePostgres();
        outbox.UseBusOutbox();
        outbox.QueryDelay = TimeSpan.FromSeconds(1);
    });

    var messaging = builder.Configuration
        .GetSection(MessagingOptions.SectionName)
        .Get<MessagingOptions>() ?? new MessagingOptions();

    switch (messaging.Transport)
    {
        case MessagingTransport.RabbitMq:
            bus.UsingRabbitMq((context, cfg) =>
            {
                var rabbit = messaging.RabbitMq;
                cfg.ConfigureJsonSerializerOptions(ClaimPortalJson.Configure);
                cfg.Host(rabbit.Host, (ushort)rabbit.Port, rabbit.VirtualHost, host =>
                {
                    host.Username(rabbit.UserName);
                    host.Password(rabbit.Password);
                });

                cfg.ConfigureEndpoints(context);
            });
            break;

        case MessagingTransport.InMemory:
            // Used by tests and by a developer running the API without Docker. The outbox
            // still writes rows; the in-memory bus is the delivery mechanism.
            bus.UsingInMemory((context, cfg) =>
            {
                cfg.ConfigureJsonSerializerOptions(ClaimPortalJson.Configure);
                cfg.ConfigureEndpoints(context);
            });
            break;

        case MessagingTransport.AzureServiceBus:
            throw new NotSupportedException(
                "The Azure Service Bus transport is added by FIR-9. Set Messaging:Transport to RabbitMq or InMemory.");

        default:
            throw new NotSupportedException($"Unknown messaging transport '{messaging.Transport}'.");
    }
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    // Never leak an exception message to a customer-facing API. The correlation id is enough
    // for an operator to find the stack trace in the logs.
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";

    await context.Response.WriteAsJsonAsync(new
    {
        type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        title = "An unexpected error occurred.",
        status = StatusCodes.Status500InternalServerError,
    });
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", async (ClaimsDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapClaimsEndpoints();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
