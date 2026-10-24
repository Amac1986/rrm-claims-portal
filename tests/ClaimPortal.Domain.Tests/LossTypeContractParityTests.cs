using Xunit;
using ClaimPortal.Domain.Claims;
using Shouldly;
using ContractLossType = ClaimPortal.Contracts.LossType;

namespace ClaimPortal.Domain.Tests;

public sealed class LossTypeContractParityTests
{
    [Fact]
    public void Domain_and_contract_loss_types_have_identical_names_and_order()
    {
        var domain = Enum.GetNames<LossType>();
        var contract = Enum.GetNames<ContractLossType>();

        domain.ShouldBe(contract);
    }
}
