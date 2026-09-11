using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlertHub.Application.Abstractions;

/// <summary>Shared serializer options for stored JSON (job payloads, outbox payloads, audit before/after): camelCase, no nulls dropped, enums as strings.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
