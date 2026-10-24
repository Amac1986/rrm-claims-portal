using Xunit;

namespace ClaimPortal.Api.IntegrationTests;

/// <summary>
/// One collection for the whole integration suite.
/// </summary>
/// <remarks>
/// A single collection gives the suite its shared container fixture and also serialises the
/// test classes: each test starts a real API host and a real message consumer, and running
/// those in parallel would make queue names and broker state part of the test result.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ClaimPortalStack>
{
    public const string Name = "claims-stack";
}
