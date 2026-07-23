using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMeow.Lab;

/// <summary>Shared JSON settings used by the local HTTP and MCP transports.</summary>
internal static class JsonTransport
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    internal static readonly JsonElement NullId =
        JsonSerializer.SerializeToElement<object?>(null, Options);

    internal static T Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("A JSON request body is required.");

        T? value = JsonSerializer.Deserialize<T>(json, Options);
        return value is null
            ? throw new InvalidDataException("The JSON request body must not be null.")
            : value;
    }

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
