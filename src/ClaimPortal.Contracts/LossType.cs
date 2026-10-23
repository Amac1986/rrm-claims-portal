namespace ClaimPortal.Contracts;

/// <summary>
/// Category of loss being claimed. Serialized as the PascalCase name on the wire,
/// in both the HTTP API and the <see cref="ClaimSubmitted"/> event.
/// </summary>
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
