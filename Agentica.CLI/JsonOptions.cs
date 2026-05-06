using System.Text.Json;
using System.Text.Json.Serialization;

internal static class JsonOptions
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
