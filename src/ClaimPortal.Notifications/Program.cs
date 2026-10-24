using ClaimPortal.Notifications;
using ClaimPortal.Notifications.Idempotency;
using ClaimPortal.Notifications.Messaging;
using ClaimPortal.Observability;
using ClaimPortal.Observability.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Compact JSON logs and an OTLP trace exporter. The worker's consumer span is a child of the
// API's request span because MassTransit carries the W3C trace context in the message.
builder.AddClaimPortalObservability("claim-portal-worker");

// Everything the worker needs is registered in these two calls; see
// NotificationsServiceCollectionExtensions for the reasoning behind each lifetime.
builder.Services.AddNotificationServices(builder.Configuration);
builder.Services.AddNotificationMessaging(builder.Configuration);

var database = builder.Configuration
    .GetSection(NotificationsDatabaseOptions.SectionName)
    .Get<NotificationsDatabaseOptions>() ?? new NotificationsDatabaseOptions();

var messaging = builder.Configuration
    .GetSection(MessagingOptions.SectionName)
    .Get<MessagingOptions>() ?? new MessagingOptions();

// Same dependency probes as the API. The worker has no ASP.NET Core pipeline, so
// WorkerHealthServer exposes /health/live and /health/ready over a small HTTP listener.
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck(
        "postgres",
        new PostgresHealthCheck(database.ConnectionString),
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

builder.Services.AddHostedService<WorkerHealthServer>();

var host = builder.Build();
await host.RunAsync();
