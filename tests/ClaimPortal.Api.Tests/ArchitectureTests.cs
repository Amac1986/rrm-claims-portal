using Shouldly;
using Xunit;

namespace ClaimPortal.Api.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void The_api_does_not_reference_an_email_library()
    {
        // The pass/fail rule in the assignment: email generation and delivery live in the
        // notification worker, never in the API. A project reference is the structural
        // version of that rule; a code review is not.
        var referencedAssemblies = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        referencedAssemblies.ShouldNotContain("MailKit");
        referencedAssemblies.ShouldNotContain("MimeKit");
    }
}
