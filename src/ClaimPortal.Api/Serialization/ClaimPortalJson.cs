using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaimPortal.Api.Serialization;

/// <summary>
/// JSON settings shared by the HTTP surface and MassTransit's System.Text.Json serializer.
/// </summary>
/// <remarks>
/// The frozen contract spells enums as PascalCase strings (section 3) and timestamps as
/// UTC with a <c>Z</c> (section 6). Configuring both surfaces from one place means an event
/// produced by the API and an event read by the worker cannot drift apart.
/// </remarks>
public static class ClaimPortalJson
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }
}
