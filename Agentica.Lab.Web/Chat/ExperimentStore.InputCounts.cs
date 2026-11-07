using System.Text.Json;
using Agentica.Clients.Llm;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Chat;

public sealed partial class ExperimentStore : IExperimentInputCountJournal
{
    private static void InitializeInputCountSchema(SqliteConnection connection)
    {
        using var command = Command(connection, "CREATE TABLE IF NOT EXISTS input_count_attempts(attempt_id TEXT PRIMARY KEY,scope_id TEXT NOT NULL,request_json TEXT NOT NULL,evidence_json TEXT NULL)");
        command.ExecuteNonQuery();
    }

    public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkKind != LlmProviderWorkKind.InputCount) throw new InvalidOperationException("Only input counts belong in this journal.");
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, "INSERT INTO input_count_attempts(attempt_id,scope_id,request_json) VALUES($id,$scope,$request)",
                ("$id", request.References.AttemptId), ("$scope", request.HostScopeId), ("$request", Serialize(request)));
            command.ExecuteNonQuery();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ObserveAsync(LlmPreparedRequestDescriptor request, JsonElement evidence)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, "UPDATE input_count_attempts SET evidence_json=$evidence WHERE attempt_id=$id AND request_json=$request",
                ("$evidence", evidence.GetRawText()), ("$id", request.References.AttemptId), ("$request", Serialize(request)));
            command.ExecuteNonQuery(); // A rejected reservation may have no row; its capture remains with its caller.
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ExperimentInputCountRecord> ReadInputCountAttempts(string scopeId, int limit = 100)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, "SELECT request_json,evidence_json FROM input_count_attempts WHERE scope_id=$scope ORDER BY rowid DESC LIMIT $limit",
                ("$scope", scopeId), ("$limit", Math.Clamp(limit, 1, 100)));
            using var reader = command.ExecuteReader();
            var rows = new List<ExperimentInputCountRecord>();
            while (reader.Read()) rows.Add(new(Deserialize<LlmPreparedRequestDescriptor>(reader.GetString(0)),
                reader.IsDBNull(1) ? null : JsonDocument.Parse(reader.GetString(1)).RootElement.Clone()));
            return rows;
        }
    }
}
