using System.Runtime.CompilerServices;
using ClaimPortal.Api.Validation;

namespace ClaimPortal.Api.Tests;

internal static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize() => ValidationConfiguration.Configure();
}
