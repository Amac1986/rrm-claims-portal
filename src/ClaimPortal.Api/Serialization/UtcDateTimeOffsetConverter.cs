using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaimPortal.Api.Serialization;

/// <summary>
/// Writes every <see cref="DateTimeOffset"/> as UTC with a trailing <c>Z</c>.
/// </summary>
/// <remarks>
/// System.Text.Json's default for a zero-offset DateTimeOffset is <c>+00:00</c>. The frozen
/// contract (section 6) says timestamps are UTC and <c>Z</c>-suffixed, and a client that
/// string-compares or regexes the value should not have to accept two spellings of the same
/// instant. Reads accept any valid offset.
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.GetDateTimeOffset();

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(
            value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}
