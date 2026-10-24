namespace ClaimPortal.Domain.Claims;

/// <summary>
/// Category of loss being claimed.
/// </summary>
/// <remarks>
/// Deliberately a separate declaration from <c>ClaimPortal.Contracts.LossType</c>, because
/// ClaimPortal.Domain depends on nothing but the BCL. The member names are identical and
/// <c>LossTypeContractParityTests</c> fails the build if the two ever drift apart.
/// </remarks>
public enum LossType
{
    Fire,
    Water,
    Wind,
    Hail,
    Theft,
    Vandalism,
    Other,
}
