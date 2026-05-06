using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;
using System.Text.Json;

namespace Agentica.CLI.Scenarios.WorkbenchQuest;

public sealed class WorkbenchQuestSession
{
    public WorkbenchQuestSession(WorkbenchScenario scenario)
    {
        Scenario = scenario;
        State = new WorkbenchRunState();
        foreach (var file in scenario.Files.Values)
        {
            State.Files[file.Path] = file.Content;
            State.InitialFiles[file.Path] = file.Content;
        }
    }

    public WorkbenchScenario Scenario { get; }

    public WorkbenchRunState State { get; }

    public ToolResult Execute(ToolInvocation invocation) =>
        invocation.ToolId switch
        {
            WorkbenchQuestToolIds.ListFiles => ListFiles(invocation),
            WorkbenchQuestToolIds.ReadFile => ReadFile(invocation),
            WorkbenchQuestToolIds.Search => Search(invocation),
            WorkbenchQuestToolIds.RunCheck => RunCheck(invocation),
            WorkbenchQuestToolIds.Diff => Diff(invocation),
            WorkbenchQuestToolIds.ApplyPatch => ApplyPatch(invocation),
            WorkbenchQuestToolIds.WriteNote => WriteNote(invocation),
            WorkbenchQuestToolIds.Complete => Complete(invocation),
            _ => Refused(invocation, "unknown_workbench_tool", $"Unknown workbench tool '{invocation.ToolId}'.")
        };

    public IReadOnlyDictionary<string, object?> PublicSnapshot() => Snapshot("snapshot");

    private ToolResult ListFiles(ToolInvocation invocation)
    {
        var data = Snapshot("list_files");
        data["files"] = Scenario.Files.Values
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = file.Path,
                ["bytes"] = State.Files[file.Path].Length,
                ["readOnly"] = file.ReadOnly
            })
            .ToArray();

        return Observed(invocation, ReceiptStatus.Succeeded, "Workbench files listed.", "Workbench file list observed.", data);
    }

    private ToolResult ReadFile(ToolInvocation invocation)
    {
        var path = ReadString(invocation, "path");
        if (!TryResolvePath(path, out var resolvedPath, out var refused))
        {
            return Refused(invocation, "invalid_path", refused);
        }

        State.ReadPaths.Add(resolvedPath);

        var data = Snapshot("read_file");
        data["path"] = resolvedPath;
        data["content"] = State.Files[resolvedPath];

        return Observed(invocation, ReceiptStatus.Succeeded, $"Read {resolvedPath}.", $"Raw file content observed for {resolvedPath}.", data);
    }

    private ToolResult Search(ToolInvocation invocation)
    {
        var query = ReadString(invocation, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return Refused(invocation, "missing_query", "Search requires a non-empty query.");
        }

        State.SearchQueries.Add(query);

        var matches = new List<Dictionary<string, object?>>();
        foreach (var pair in State.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var lines = pair.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = pair.Key,
                        ["line"] = index + 1,
                        ["text"] = lines[index]
                    });
                }
            }
        }

        var data = Snapshot("search");
        data["query"] = query;
        data["matches"] = matches.Take(20).ToArray();
        data["truncated"] = matches.Count > 20;

        return Observed(invocation, ReceiptStatus.Succeeded, $"Search returned {matches.Count} raw matches.", "Workbench search matches observed.", data);
    }

    private ToolResult RunCheck(ToolInvocation invocation)
    {
        var (passed, output) = Scenario.Descriptor.ScenarioId switch
        {
            "broken_check" => CheckBrokenCheck(),
            "missing_mapping" => CheckMissingMapping(),
            "structured_doc_merge" => CheckStructuredDocMerge(),
            "word_ladder" => CheckWordLadder(),
            "release_gate" => CheckReleaseGate(),
            _ => (false, $"FAIL unknown scenario checker: {Scenario.Descriptor.ScenarioId}")
        };

        var record = new WorkbenchCheckRecord(State.CheckHistory.Count + 1, State.NextActionOrder++, passed, output);
        State.CheckHistory.Add(record);

        var data = Snapshot("run_check");
        data["status"] = passed ? "passed" : "failed";
        data["output"] = output;
        data["checkNumber"] = record.Number;

        return Observed(invocation, ReceiptStatus.Succeeded, $"Workbench check {(passed ? "passed" : "failed")}.", "Workbench check output observed.", data);
    }

    private (bool Passed, string Output) CheckBrokenCheck()
    {
        var source = State.Files["src/Calculator.txt"];
        var addWorks = source.Contains("return left + right", StringComparison.Ordinal);
        var multiplyWorks = source.Contains("return left * right", StringComparison.Ordinal);
        var passed = addWorks && multiplyWorks;
        var output = passed
            ? """
              PASS tests/CalculatorTests.txt
                Add combines two values: expected 5, actual 5
                Multiply combines repeated values: expected 6, actual 6
              """
            : """
              FAIL tests/CalculatorTests.txt
                Add combines two values
                  expression: Calculator.Add(2, 3)
                  expected: 5
                  actual: -1
                Multiply combines repeated values: expected 6, actual 6
              """;

        return (passed, output);
    }

    private (bool Passed, string Output) CheckMissingMapping()
    {
        var mapping = ParseCsvMap(State.Files["config/mapping.csv"]);
        var orders = ParseCsvRows(State.Files["input/orders.csv"]);
        var expected = ParseCsvRows(State.Files["expected/output.csv"]);
        var generated = orders
            .Select(order =>
            {
                var code = order["method_code"];
                return new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["order_id"] = order["order_id"],
                    ["shipping_label"] = mapping.TryGetValue(code, out var label) ? label : "UNMAPPED"
                };
            })
            .ToArray();

        var mismatches = new List<string>();
        for (var index = 0; index < expected.Count; index++)
        {
            var expectedRow = expected[index];
            var actualRow = generated[index];
            if (!string.Equals(expectedRow["shipping_label"], actualRow["shipping_label"], StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"  row {index + 1} order_id {expectedRow["order_id"]} field shipping_label expected {expectedRow["shipping_label"]} actual {actualRow["shipping_label"]}");
            }
        }

        if (mismatches.Count == 0)
        {
            return (
                true,
                """
                PASS output comparison
                  3 rows matched expected/output.csv
                """);
        }

        return (
            false,
            "FAIL output comparison\n" + string.Join('\n', mismatches));
    }

    private (bool Passed, string Output) CheckStructuredDocMerge()
    {
        var merged = NormalizeText(State.Files["merged.md"]);
        var expected = NormalizeText(
            """
            <!-- section:id=intro -->
            Welcome to the account guide. Keep your profile email current.

            <!-- section:id=billing -->
            Billing happens on the first business day of each month.

            <!-- section:id=support -->
            Contact support within 30 days to dispute an invoice.
            """);
        var requiredSections = new[] { "intro", "billing", "support" };
        var errors = new List<string>();

        foreach (var sectionId in requiredSections)
        {
            var marker = $"<!-- section:id={sectionId} -->";
            var count = CountOccurrences(merged, marker);
            if (count != 1)
            {
                errors.Add($"  section {sectionId} count expected 1 actual {count}");
            }
        }

        if (merged.Contains("section:id=legacy", StringComparison.Ordinal))
        {
            errors.Add("  section legacy should be absent");
        }

        if (!string.Equals(merged, expected, StringComparison.Ordinal))
        {
            errors.Add("  merged.md content does not match rule-resolved expected sections");
        }

        if (errors.Count == 0)
        {
            return (
                true,
                """
                PASS structured document merge
                  sections intro, billing, support matched merge rules
                  section legacy absent
                """);
        }

        return (
            false,
            "FAIL structured document merge\n" + string.Join('\n', errors.Distinct(StringComparer.Ordinal)));
    }

    private (bool Passed, string Output) CheckWordLadder()
    {
        try
        {
            using var document = JsonDocument.Parse(State.Files["answer.json"]);
            if (!document.RootElement.TryGetProperty("ladder", out var ladderElement) ||
                ladderElement.ValueKind != JsonValueKind.Array)
            {
                return (false, "FAIL word ladder\n  answer.json must contain array property ladder");
            }

            var ladder = ladderElement.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
            var dictionary = State.Files["dictionary.txt"]
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            var errors = new List<string>();

            if (ladder.Length < 2)
            {
                errors.Add("  ladder must contain at least two words");
            }

            if (ladder.FirstOrDefault() != "cold")
            {
                errors.Add($"  first word expected cold actual {ladder.FirstOrDefault() ?? "<missing>"}");
            }

            if (ladder.LastOrDefault() != "warm")
            {
                errors.Add($"  last word expected warm actual {ladder.LastOrDefault() ?? "<missing>"}");
            }

            for (var index = 0; index < ladder.Length; index++)
            {
                var word = ladder[index];
                if (word.Length != 4 || !word.All(char.IsLower))
                {
                    errors.Add($"  word {index + 1} must be lowercase and four letters: {word}");
                }

                if (!dictionary.Contains(word))
                {
                    errors.Add($"  word {index + 1} not in dictionary: {word}");
                }
            }

            for (var index = 0; index < ladder.Length - 1; index++)
            {
                var distance = HammingDistance(ladder[index], ladder[index + 1]);
                if (distance != 1)
                {
                    errors.Add($"  transition {ladder[index]} -> {ladder[index + 1]} differs by {distance} letters");
                }
            }

            if (errors.Count == 0)
            {
                return (
                    true,
                    $"PASS word ladder\n  valid ladder length {ladder.Length}: {string.Join(" -> ", ladder)}");
            }

            return (false, "FAIL word ladder\n" + string.Join('\n', errors));
        }
        catch (JsonException exception)
        {
            return (false, $"FAIL word ladder\n  answer.json is not valid JSON: {exception.Message}");
        }
    }

    private (bool Passed, string Output) CheckReleaseGate()
    {
        var errors = new List<string>();
        var frontend = State.Files["services/frontend.env"];
        var backend = State.Files["services/backend.routes"];
        var manifest = State.Files["release/manifest.txt"];

        if (!frontend.Contains("API_BASE=/prod", StringComparison.Ordinal))
        {
            errors.Add("  services/frontend.env API_BASE expected /prod");
        }

        if (!backend.Contains("GET /health -> 200", StringComparison.Ordinal))
        {
            errors.Add("  services/backend.routes health route expected 200");
        }

        if (!manifest.Contains("CHANGELOG.md: included", StringComparison.Ordinal))
        {
            errors.Add("  release/manifest.txt must include CHANGELOG.md");
        }

        if (errors.Count == 0)
        {
            return (
                true,
                """
                PASS release gate
                  frontend API target, backend health route, and manifest entries are release-ready
                """);
        }

        return (
            false,
            "FAIL release gate\n" + string.Join('\n', errors));
    }

    private ToolResult Diff(ToolInvocation invocation)
    {
        var changed = ChangedFiles()
            .Select(path => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["diff"] = BuildSimpleDiff(path)
            })
            .ToArray();

        var data = Snapshot("diff");
        data["changedFiles"] = changed;

        return Observed(invocation, ReceiptStatus.Succeeded, $"Workbench diff returned {changed.Length} changed file(s).", "Workbench diff observed.", data);
    }

    private ToolResult ApplyPatch(ToolInvocation invocation)
    {
        var mutationBlockers = MutationBlockers();
        if (mutationBlockers.Count > 0)
        {
            return Refused(
                invocation,
                "mutation_precondition_missing",
                "Patch refused because required pre-mutation evidence is missing.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["mutationBlockers"] = mutationBlockers,
                    ["requiredBeforeMutation"] = new[]
                    {
                        "failed baseline workbench.run_check observation",
                        "read observation for at least one relevant evidence file"
                    }
                });
        }

        var path = ReadString(invocation, "path");
        if (!TryResolvePath(path, out var resolvedPath, out var refused))
        {
            return Refused(invocation, "invalid_path", refused);
        }

        var file = Scenario.Files[resolvedPath];
        if (file.ReadOnly)
        {
            return Refused(invocation, "read_only_file", $"File '{resolvedPath}' is read-only.");
        }

        var find = ReadString(invocation, "find");
        var replace = ReadString(invocation, "replace");
        if (string.IsNullOrEmpty(find))
        {
            return Refused(invocation, "missing_find", "Patch requires exact text in the find field.");
        }

        if (replace is null)
        {
            return Refused(invocation, "missing_replace", "Patch requires replacement text.");
        }

        var current = State.Files[resolvedPath];
        if (!current.Contains(find, StringComparison.Ordinal))
        {
            return Refused(invocation, "find_text_not_found", $"File '{resolvedPath}' does not contain the exact find text.");
        }

        State.Files[resolvedPath] = current.Replace(find, replace, StringComparison.Ordinal);
        var record = new WorkbenchPatchRecord(State.PatchHistory.Count + 1, State.NextActionOrder++, resolvedPath, find, replace, Applied: true, "Patch applied.");
        State.PatchHistory.Add(record);

        var data = Snapshot("apply_patch");
        data["path"] = resolvedPath;
        data["changedPaths"] = new[] { resolvedPath };
        data["patchNumber"] = record.Number;
        data["rationale"] = ReadString(invocation, "rationale");
        data["diff"] = BuildSimpleDiff(resolvedPath);

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Applied patch to {resolvedPath}.", data);
        var observation = Observation(invocation, receipt, $"Patch changed {resolvedPath}.", data);
        return new ToolResult(receipt, observation);
    }

    private ToolResult WriteNote(ToolInvocation invocation)
    {
        var note = ReadString(invocation, "note");
        if (string.IsNullOrWhiteSpace(note))
        {
            return Refused(invocation, "missing_note", "Write note requires non-empty note text.");
        }

        var record = new WorkbenchNoteRecord(State.Notes.Count + 1, note);
        State.Notes.Add(record);

        var data = Snapshot("write_note");
        data["noteNumber"] = record.Number;
        data["note"] = record.Note;

        return Observed(invocation, ReceiptStatus.Succeeded, "Workbench note written.", "Workbench note observed.", data);
    }

    private ToolResult Complete(ToolInvocation invocation)
    {
        var blockers = CompletionBlockers();
        if (blockers.Count > 0)
        {
            return Refused(
                invocation,
                "objective_not_satisfied",
                "Workbench objective is not satisfied.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["completionBlockers"] = blockers
                });
        }

        State.Completed = true;
        var data = Snapshot("complete");
        data["objectiveCompleted"] = true;
        data["completionEvidence"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["failedCheckBeforePatch"] = true,
            ["readRelevantFile"] = true,
            ["appliedPatch"] = true,
            ["passingCheckAfterPatch"] = true
        };

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Workbench objective completed.", data);
        var artifact = new Artifact(
            ArtifactId: AgenticaIds.New("artifact"),
            Kind: "workbench.objective_completed",
            Payload: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

        return new ToolResult(receipt, Artifact: artifact);
    }

    private IReadOnlyList<string> CompletionBlockers()
    {
        var blockers = new List<string>();
        var firstPatch = State.PatchHistory.FirstOrDefault(item => item.Applied);
        if (firstPatch is null)
        {
            blockers.Add("No applied patch receipt exists.");
        }

        if (!State.CheckHistory.Any(item => !item.Passed) ||
            (firstPatch is not null && !State.CheckHistory.Any(item => !item.Passed && item.ActionOrder < firstPatch.ActionOrder)))
        {
            blockers.Add("No failed check observation exists before mutation.");
        }

        if (!Scenario.RelevantPaths.Any(path => State.ReadPaths.Contains(path)))
        {
            blockers.Add("No relevant source or test file was read.");
        }

        if (!State.CheckHistory.Any(item => item.Passed) ||
            (firstPatch is not null && !State.CheckHistory.Any(item => item.Passed && item.ActionOrder > firstPatch.ActionOrder)))
        {
            blockers.Add("No passing check observation exists after mutation.");
        }

        return blockers;
    }

    private IReadOnlyList<string> MutationBlockers()
    {
        var blockers = new List<string>();

        if (!State.CheckHistory.Any(item => !item.Passed))
        {
            blockers.Add("No failed baseline check observation exists. Call workbench.run_check before mutation.");
        }

        if (!Scenario.RelevantPaths.Any(path => State.ReadPaths.Contains(path)))
        {
            blockers.Add("No relevant evidence file was read. Read at least one relevant scenario file before mutation.");
        }

        return blockers;
    }

    private Dictionary<string, object?> Snapshot(string action) =>
        new(StringComparer.Ordinal)
        {
            ["scenarioId"] = Scenario.Descriptor.ScenarioId,
            ["title"] = Scenario.Descriptor.Title,
            ["objective"] = Scenario.Descriptor.Objective,
            ["action"] = action,
            ["readPaths"] = State.ReadPaths.Order(StringComparer.Ordinal).ToArray(),
            ["searchQueries"] = State.SearchQueries.Order(StringComparer.Ordinal).ToArray(),
            ["checkHistory"] = State.CheckHistory
                .Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["number"] = item.Number,
                    ["order"] = item.ActionOrder,
                    ["status"] = item.Passed ? "passed" : "failed"
                })
                .ToArray(),
            ["patchHistory"] = State.PatchHistory
                .Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["number"] = item.Number,
                    ["order"] = item.ActionOrder,
                    ["path"] = item.Path,
                    ["applied"] = item.Applied
                })
                .ToArray(),
            ["notes"] = State.Notes.Select(item => item.Number).ToArray(),
            ["objectiveCompleted"] = State.Completed
        };

    private IReadOnlyList<string> ChangedFiles() =>
        State.Files
            .Where(pair => !string.Equals(pair.Value, State.InitialFiles[pair.Key], StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<Dictionary<string, string>> ParseCsvRows(string csv)
    {
        var lines = csv
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        var headers = lines[0].Split(',').Select(item => item.Trim()).ToArray();
        return lines.Skip(1)
            .Select(line =>
            {
                var values = line.Split(',').Select(item => item.Trim()).ToArray();
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var index = 0; index < headers.Length && index < values.Length; index++)
                {
                    row[headers[index]] = values[index];
                }

                return row;
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> ParseCsvMap(string csv) =>
        ParseCsvRows(csv)
            .Where(row => row.ContainsKey("method_code") && row.ContainsKey("label"))
            .ToDictionary(row => row["method_code"], row => row["label"], StringComparer.Ordinal);

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(value, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            start = index + value.Length;
        }
    }

    private static int HammingDistance(string left, string right)
    {
        var maxLength = Math.Max(left.Length, right.Length);
        var distance = 0;
        for (var index = 0; index < maxLength; index++)
        {
            var leftChar = index < left.Length ? left[index] : '\0';
            var rightChar = index < right.Length ? right[index] : '\0';
            if (leftChar != rightChar)
            {
                distance++;
            }
        }

        return distance;
    }

    private string BuildSimpleDiff(string path) =>
        $"""
        --- initial/{path}
        +++ current/{path}
        - {State.InitialFiles[path].Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n- ", StringComparison.Ordinal)}
        + {State.Files[path].Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\n+ ", StringComparison.Ordinal)}
        """;

    private bool TryResolvePath(string? path, out string resolvedPath, out string message)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            message = "Path is required.";
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(normalized) ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.StartsWith("/", StringComparison.Ordinal))
        {
            message = $"Path '{path}' is outside the scenario sandbox.";
            return false;
        }

        if (!State.Files.ContainsKey(normalized))
        {
            message = $"Path '{path}' does not exist in the scenario.";
            return false;
        }

        resolvedPath = normalized;
        message = string.Empty;
        return true;
    }

    private ToolResult Observed(
        ToolInvocation invocation,
        ReceiptStatus status,
        string receiptMessage,
        string observationSummary,
        IReadOnlyDictionary<string, object?> data)
    {
        var receipt = Receipt(invocation, status, receiptMessage, data);
        return new ToolResult(receipt, Observation(invocation, receipt, observationSummary, data));
    }

    private ToolResult Refused(
        ToolInvocation invocation,
        string reason,
        string message,
        IReadOnlyDictionary<string, object?>? extraData = null)
    {
        var data = Snapshot("refused");
        data["reason"] = reason;
        data["blocker"] = reason;

        if (extraData is not null)
        {
            foreach (var pair in extraData)
            {
                data[pair.Key] = pair.Value;
            }
        }

        var receipt = Receipt(invocation, ReceiptStatus.Refused, message, data);
        return new ToolResult(receipt, Observation(invocation, receipt, message, data));
    }

    private static string? ReadString(ToolInvocation invocation, string key) =>
        invocation.Input.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        string message,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            ReceiptId: AgenticaIds.New("receipt"),
            StepId: invocation.StepId,
            ToolId: invocation.ToolId,
            Status: status,
            Message: message,
            At: DateTimeOffset.UtcNow,
            Data: data);

    private static Observation Observation(
        ToolInvocation invocation,
        Receipt receipt,
        string summary,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            ObservationId: AgenticaIds.New("observation"),
            StepId: invocation.StepId,
            Kind: ObservationKind.ToolResult,
            Summary: summary,
            Data: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);
}
