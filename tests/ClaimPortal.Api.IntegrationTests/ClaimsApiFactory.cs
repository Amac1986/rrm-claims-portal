using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimPortal.Api.IntegrationTests;

/// <summary>
/// Boots the real API — the real <c>Program.cs</c>, the real mass-transit outbox — against
/// a throwaway database and the fixture's RabbitMQ container.
/// </summary>
/// <remarks>
/// Nothing is stubbed in the general case. Only the collision tests replace the clock and
/// the claim-number generator, so they can force a known duplicate; even then the retry
/// policy, repository, outbox and broker are the production ones. Overriding the
/// repository or the bus would defeat the point of the suite.
/// </remarks>
internal sealed class ClaimsApiFactory : WebApplicationFactory<Program>
{
    private readonly ClaimPortalStack _stack;
    private readonly string _connectionString;
    private readonly Action<IServiceCollection>? _configureTestServices;

    public ClaimsApiFactory(
        ClaimPortalStack stack,
        string connectionString,
        Action<IServiceCollection>? configureTestServices = null)
    {
        _stack = stack;
        _connectionString = connectionString;
        _configureTestServices = configureTestServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production so the API's own exception handler and ProblemDetails path are the
        // ones under test, not the developer exception page.
        builder.UseEnvironment("Production");

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Claims"] = _connectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Messaging:Transport"] = "RabbitMq",
            ["Messaging:RabbitMq:Host"] = _stack.RabbitMq.Hostname,
            ["Messaging:RabbitMq:Port"] =
                _stack.RabbitMq.GetMappedPublicPort(ClaimPortalStack.RabbitMqPort)
                    .ToString(CultureInfo.InvariantCulture),
            ["Messaging:RabbitMq:VirtualHost"] = "/",
            ["Messaging:RabbitMq:UserName"] = ClaimPortalStack.RabbitMqUserName,
            ["Messaging:RabbitMq:Password"] = ClaimPortalStack.RabbitMqPassword,

            // The collision tests deliberately provoke unique violations, and EF logs each
            // one at Error. Suppress the data-access category so the expected failures do
            // not bury the test result; assertion messages still carry the real exception.
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "None",
            ["Logging:LogLevel:MassTransit"] = "Warning",
        };

        // Minimal hosting reads configuration while Program.cs is running, before the host
        // is built. Feeding the values through both the deferred web-host settings and the
        // app configuration keeps this independent of when WebApplicationFactory applies
        // each hook.
        foreach (var setting in settings)
        {
            builder.UseSetting(setting.Key, setting.Value);
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(settings));

        if (_configureTestServices is not null)
        {
            builder.ConfigureTestServices(_configureTestServices);
        }
    }
}
