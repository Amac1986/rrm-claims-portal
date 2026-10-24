using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace ClaimPortal.Api.IntegrationTests;

/// <summary>
/// The containers every integration test needs, started once for the collection and torn
/// down when the last test finishes.
/// </summary>
/// <remarks>
/// <para>
/// Both image tags are pinned here to the exact images the repository runs in
/// <c>docker-compose.yml</c> / <c>docker/rabbitmq/Dockerfile</c>: <c>postgres:17-alpine</c>
/// and <c>rabbitmq:4.1-management-alpine</c>. Using the Testcontainers module defaults
/// instead would let a package bump silently change the database or broker under the tests,
/// which is precisely the "works only on my machine" drift this suite exists to catch.
/// </para>
/// <para>
/// The compose RabbitMQ image is this same base image plus the delayed-message-exchange
/// plugin. These tests only cover the publish path, which does not use delayed delivery;
/// anything that later exercises the worker's delayed redelivery should build and run the
/// compose image instead of widening this fixture.
/// </para>
/// <para>
/// The containers do not use host ports 5432/5672, so the suite neither depends on nor
/// collides with a developer's running compose stack.
/// </para>
/// </remarks>
public sealed class ClaimPortalStack : IAsyncLifetime
{
    public const string PostgresImage = "postgres:17-alpine";
    public const string RabbitMqImage = "rabbitmq:4.1-management-alpine";

    // RabbitMQ refuses the built-in "guest" account from outside the container, so the
    // module's own throwaway credentials are used explicitly.
    public const string RabbitMqUserName = "rabbitmq";
    public const string RabbitMqPassword = "rabbitmq";
    public const int RabbitMqPort = 5672;

    public const string PostgresDatabase = "claimportal";
    public const string PostgresUserName = "claimportal";
    public const string PostgresPassword = "claimportal";

    private PostgreSqlContainer? _postgres;
    private RabbitMqContainer? _rabbitMq;

    public PostgreSqlContainer Postgres =>
        _postgres ?? throw new InvalidOperationException("The PostgreSQL container is not started.");

    public RabbitMqContainer RabbitMq =>
        _rabbitMq ?? throw new InvalidOperationException("The RabbitMQ container is not started.");

    public async ValueTask InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder(PostgresImage)
            .WithDatabase(PostgresDatabase)
            .WithUsername(PostgresUserName)
            .WithPassword(PostgresPassword)
            .Build();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername(RabbitMqUserName)
            .WithPassword(RabbitMqPassword)
            .Build();

        // Independent containers, so there is no reason to pay their startup cost serially.
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_rabbitMq is not null)
        {
            await _rabbitMq.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}
