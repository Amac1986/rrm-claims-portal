using ClaimPortal.Domain.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClaimPortal.Infrastructure.Persistence;

/// <summary>
/// Maps <see cref="Claim"/> onto the <c>claims</c> table.
/// </summary>
/// <remarks>
/// Column lengths mirror the contract's validation rules. They are a second line of
/// defence, not the primary one: a <c>varchar(32)</c> overflow is a 500 with a Postgres
/// message, whereas the validator produces the per-field 400 the React form can render.
/// The constraint is here so that a future caller that bypasses the validator cannot
/// quietly widen the data.
/// </remarks>
internal sealed class ClaimConfiguration : IEntityTypeConfiguration<Claim>
{
    /// <summary>Name of the unique index on <c>claim_number</c>; the retry keys off it.</summary>
    public const string ClaimNumberIndexName = "ix_claims_claim_number";

    public void Configure(EntityTypeBuilder<Claim> builder)
    {
        builder.ToTable("claims");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.ClaimNumber)
            .HasConversion(
                claimNumber => claimNumber.Value,
                value => ClaimNumber.Parse(value))
            .HasMaxLength(ClaimNumber.TotalLength)
            .IsRequired();

        // The uniqueness guarantee. Everything else about claim numbers — the CSPRNG, the
        // keyspace, the retry — is an optimisation that keeps this index from being hit.
        builder.HasIndex(c => c.ClaimNumber)
            .IsUnique()
            .HasDatabaseName(ClaimNumberIndexName);

        builder.Property(c => c.PolicyNumber).HasMaxLength(32).IsRequired();

        builder.Property(c => c.IncidentDate)
            // date, not timestamp: an incident happened on a calendar day. Storing it as
            // a timestamp would invent a time of day and a timezone the customer never
            // gave us, and would shift the date for anyone east or west of the server.
            .HasColumnType("date")
            .IsRequired();

        builder.Property(c => c.LossType)
            // Stored as text rather than an ordinal. The numbers are meaningless in a
            // psql session and in a report, and reordering the enum would silently
            // relabel every historical row.
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(c => c.Description).HasMaxLength(2000).IsRequired();

        builder.Property(c => c.EstimatedLossAmount)
            // numeric(14,2): money, so never float. Comfortably above the contract's
            // 10,000,000 ceiling.
            .HasPrecision(14, 2);

        builder.Property(c => c.SubmittedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(c => c.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.ComplexProperty(c => c.Claimant, claimant =>
        {
            claimant.Property(x => x.FirstName).HasColumnName("claimant_first_name").HasMaxLength(64).IsRequired();
            claimant.Property(x => x.LastName).HasColumnName("claimant_last_name").HasMaxLength(64).IsRequired();
            claimant.Property(x => x.Email).HasColumnName("claimant_email").HasMaxLength(254).IsRequired();
            claimant.Property(x => x.Phone).HasColumnName("claimant_phone").HasMaxLength(20).IsRequired();
        });

        // Complex properties, not owned entities: the address and the claimant are values
        // belonging to the claim, with no identity and no lifetime of their own. Owned
        // entities would model the same columns but drag in a shadow key, change
        // tracking and the possibility of a null reference where the domain says there
        // cannot be one.
        builder.ComplexProperty(c => c.PropertyAddress, address =>
        {
            address.Property(x => x.AddressLine1).HasColumnName("property_address_line1").HasMaxLength(128).IsRequired();
            address.Property(x => x.AddressLine2).HasColumnName("property_address_line2").HasMaxLength(128);
            address.Property(x => x.City).HasColumnName("property_city").HasMaxLength(64).IsRequired();
            address.Property(x => x.Province)
                .HasColumnName("property_province")
                .HasConversion<string>()
                .HasMaxLength(2)
                .IsRequired();
            address.Property(x => x.PostalCode)
                .HasColumnName("property_postal_code")
                .HasConversion(
                    postalCode => postalCode.Value,
                    value => PostalCode.Parse(value))
                .HasMaxLength(PostalCode.CanonicalLength)
                .IsRequired();
        });
    }
}
