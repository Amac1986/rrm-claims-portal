using System.Text.Json.Serialization;
using ClaimPortal.Api;
using ClaimPortal.Api.Messaging;
using ClaimPortal.Api.Observability;
using ClaimPortal.Api.Serialization;
using ClaimPortal.Api.Validation;
using ClaimPortal.Domain.Claims;
using ClaimPortal.Infrastructure.Persistence;
using ClaimPortal.Observability;
using ClaimPortal.Observability.HealthChecks;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog writes compact JSON to stdout; OpenTelemetry exports one OTLP trace stream. The
// exporter targets Honeycomb when HONEYCOMB_API_KEY is present and the local collector
// otherwise, so `docker compose up` needs no account.
builder.AddClaimPortalObservability("claim-portal-api", tracing => tracing
    .AddAspNetCoreInstrumentation(options =>
    {
        // Health probes every few seconds would bury the claim trace in noise.
        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
    })
    .AddHttpClientInstrumentation());

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

// Read once: both the bus and the readiness probe need the same broker settings.
var messaging = builder.Configuration
    .GetSection(MessagingOptions.SectionName)
    .Get<MessagingOptions>() ?? new MessagingOptions();

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
            // Same outbox, same event, same entity names as RabbitMQ; only the broker client
            // changes. `ConfigureEndpoints` is called here too so the publish topology is built
            // by the transport-neutral code path rather than by anything Service Bus-specific.
            bus.UsingAzureServiceBus((context, cfg) =>
            {
                cfg.ConfigureJsonSerializerOptions(ClaimPortalJson.Configure);
                ServiceBusHostConfiguration.Configure(cfg, messaging.AzureServiceBus);
                cfg.ConfigureEndpoints(context);
            });
            break;

        default:
            throw new NotSupportedException($"Unknown messaging transport '{messaging.Transport}'.");
    }
});

// Liveness is intentionally process-local: it answers "is this process running?" Readiness
// opens a real Npgsql connection and, on RabbitMQ, a real AMQP connection. A dependency being
// down must turn the probe red, not return a hard-coded 200.
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck(
        "postgres",
        new PostgresHealthCheck(connectionString),
        tags: ["ready"]);

if (messaging.Transport == MessagingTransport.RabbitMq)
{
    healthChecks.AddCheck(
        "rabbitmq",
        new RabbitMqHealthCheck(new RabbitMqHealthOptions
        {
            Host = messaging.RabbitMq.Host,
            Port = messaging.RabbitMq.Port,
            VirtualHost = messaging.RabbitMq.VirtualHost,
            UserName = messaging.RabbitMq.UserName,
            Password = messaging.RabbitMq.Password,
        }),
        tags: ["ready"]);
}

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        diagnosticContext.Set(CorrelationId.LogPropertyName, httpContext.TraceIdentifier);
});

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
        correlationId = context.TraceIdentifier,
    });
}));

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteJson,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteJson,
});

app.MapClaimsEndpoints();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory-based tests.</summary>
public partial class Program;
