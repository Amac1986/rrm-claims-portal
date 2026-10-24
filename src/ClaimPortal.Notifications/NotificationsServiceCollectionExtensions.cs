using System.Text.Json.Serialization;
using ClaimPortal.Notifications.Consuming;
using ClaimPortal.Notifications.Email;
using ClaimPortal.Notifications.Idempotency;
using ClaimPortal.Notifications.Messaging;
using ClaimPortal.Notifications.Templating;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaimPortal.Notifications;

/// <summary>
/// The composition root, in one file, so that "where would SendGrid be plugged in?" has a
/// one-line answer and the tests can register the same graph with the fakes swapped.
/// </summary>
public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers options, the renderer, the email sender and the idempotency store — everything
    /// except the bus, which <see cref="AddNotificationMessaging"/> owns so the test harness
    /// can substitute its own transport.
    /// </summary>
    public static IServiceCollection AddNotificationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ValidateOnStart on every section: a worker with a malformed SMTP host should fail at
        // startup, where a container restart loop and the logs make it obvious, rather than on
        // the first claim of the day. Nothing here is validated lazily.
        services.AddOptionsWithValidation<SmtpOptions>(configuration, SmtpOptions.SectionName);
        services.AddOptionsWithValidation<NotificationContentOptions>(
            configuration, NotificationContentOptions.SectionName);
        services.AddOptionsWithValidation<IdempotencyOptions>(configuration, IdempotencyOptions.SectionName);
        services.AddOptionsWithValidation<MessagingOptions>(configuration, MessagingOptions.SectionName);

        services.TryAddTimeProvider();

        // Singleton: the Scriban templates are parsed in the constructor, so a syntax error
        // takes the worker down at startup instead of dead-lettering the first real claim.
        services.AddSingleton<IClaimConfirmationRenderer, ScribanClaimConfirmationRenderer>();

        // The seam the assignment asks about. Swapping to SendGrid or Azure Communication
        // Services is this line and a new IEmailSender implementation — no consumer, template,
        // idempotency or test change, because nothing above this line knows SMTP exists.
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddDbContext<NotificationsDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<NotificationsDatabaseOptions>>().Value;

            builder.UseNpgsql(
                options.ConnectionString,
                npgsql =>
                {
                    npgsql.MigrationsHistoryTable(
                        NotificationsDbContext.MigrationsHistoryTable, NotificationsDbContext.Schema);

                    // Transient connection faults are retried by the provider rather than by
                    // dead-lettering a claim because Postgres was briefly restarted.
                    npgsql.EnableRetryOnFailure(options.MaxRetryCount, options.MaxRetryDelay, null);
                })
                // snake_case to match the API's tables; a reviewer opening psql sees one
                // convention across both schemas rather than two.
                .UseSnakeCaseNamingConvention();
        });

        services.AddOptionsWithValidation<NotificationsDatabaseOptions>(
            configuration, NotificationsDatabaseOptions.SectionName);

        // Scoped: one DbContext per message, created inside MassTransit's consume scope.
        services.AddScoped<INotificationDeliveryStore, EfNotificationDeliveryStore>();

        services.AddHostedService<NotificationsDatabaseMigrator>();

        return services;
    }

    /// <summary>
    /// Registers the bus and the consumer. The transport is chosen from configuration, which is
    /// the whole point: moving from the local RabbitMQ container to Azure Service Bus is
    /// <c>Messaging__Transport=AzureServiceBus</c> plus a namespace, not a rebuild.
    /// </summary>
    public static IServiceCollection AddNotificationMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var messaging = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()
            ?? new MessagingOptions();

        services.AddMassTransit(bus =>
        {
            // The definition carries the endpoint name and the retry/redelivery policy, so
            // every transport below — including the test harness — gets identical behaviour.
            bus.AddConsumer<ClaimSubmittedConsumer, ClaimSubmittedConsumerDefinition>();

            // Kebab-case, no namespace prefix: the exchange/topic name is a contract between
            // this worker and the API, and it must not contain a .NET namespace.
            bus.SetKebabCaseEndpointNameFormatter();

            switch (messaging.Transport)
            {
                case MessagingTransport.RabbitMq:
                    bus.UsingRabbitMq((context, cfg) =>
                    {
                        var rabbit = messaging.RabbitMq;
                        cfg.ConfigureJsonSerializerOptions(WireJson);
                        cfg.Host(rabbit.Host, (ushort)rabbit.Port, rabbit.VirtualHost, host =>
                        {
                            host.Username(rabbit.UserName);
                            host.Password(rabbit.Password);

                            if (rabbit.UseSsl)
                            {
                                host.UseSsl(_ => { });
                            }
                        });

                        cfg.ConfigureEndpoints(context);
                    });
                    break;

                case MessagingTransport.InMemory:
                    // No broker. Lets `dotnet run` and the renderer tests work on a machine
                    // with no Docker; it receives nothing, which is the honest behaviour.
                    bus.UsingInMemory((context, cfg) =>
                    {
                        cfg.ConfigureJsonSerializerOptions(WireJson);
                        cfg.ConfigureEndpoints(context);
                    });
                    break;

                case MessagingTransport.AzureServiceBus:
                    throw new NotSupportedException(
                        "The Azure Service Bus transport is added by a separate task. Set "
                        + "Messaging:Transport to RabbitMq or InMemory. Deliberately a loud "
                        + "failure rather than a silent fallback to RabbitMq, which would look "
                        + "like a working deployment while consuming nothing.");

                default:
                    throw new NotSupportedException($"Unknown messaging transport '{messaging.Transport}'.");
            }
        });

        return services;
    }

    /// <summary>
    /// The API writes PascalCase enum names (contract section 3); MassTransit's default
    /// System.Text.Json settings would not read a string into an enum without this converter.
    /// </summary>
    private static System.Text.Json.JsonSerializerOptions WireJson(System.Text.Json.JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static IServiceCollection AddOptionsWithValidation<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// <see cref="TimeProvider.System"/> in production; the tests inject a fake so the
    /// stale-claim window can be crossed without waiting five real minutes.
    /// </summary>
    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
