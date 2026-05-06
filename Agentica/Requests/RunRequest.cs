using System.Text.Json.Serialization;

namespace Agentica.Requests;

public sealed record RunRequest(
    string Objective,
    RequestOrigin Origin = RequestOrigin.User,
    IReadOnlyDictionary<string, object?>? Context = null)
{
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Objective);
}
