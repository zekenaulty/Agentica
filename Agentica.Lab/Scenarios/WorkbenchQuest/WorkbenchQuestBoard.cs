namespace Agentica.Lab.Scenarios.WorkbenchQuest;

public interface IWorkbenchQuestBoard
{
    IReadOnlyList<WorkbenchScenarioDescriptor> ListScenarios();

    WorkbenchScenario Load(string scenarioId);
}

public sealed class WorkbenchQuestBoard : IWorkbenchQuestBoard
{
    private readonly WorkbenchScenarioDescriptor[] _descriptors =
    [
        new(
            ScenarioId: "broken_check",
            Title: "The Broken Check",
            Objective: "The check is failing. Find and fix the problem, then verify.",
            Description: "A tiny raw-file workbench where a deterministic validation check fails until the source is corrected.",
            Difficulty: "Intro",
            EstimatedSteps: 6),
        new(
            ScenarioId: "missing_mapping",
            Title: "The Missing Mapping",
            Objective: "The pipeline output is wrong. Find the bad mapping, fix it, and verify.",
            Description: "A structured data mapping repair where the checker compares generated output against expected output.",
            Difficulty: "Intro",
            EstimatedSteps: 7),
        new(
            ScenarioId: "structured_doc_merge",
            Title: "The Structured Document Merge",
            Objective: "Merge the two document revisions according to the merge rules, then verify.",
            Description: "A Markdown section merge with explicit section ids and mechanically checkable conflict rules.",
            Difficulty: "Moderate",
            EstimatedSteps: 8),
        new(
            ScenarioId: "word_ladder",
            Title: "The Word Ladder",
            Objective: "Find a valid word ladder from cold to warm using the dictionary and verify it.",
            Description: "A constrained word puzzle where each step must be a dictionary word and differ by one letter.",
            Difficulty: "Moderate",
            EstimatedSteps: 7),
        new(
            ScenarioId: "release_gate",
            Title: "The Release Gate",
            Objective: "Collect independent release evidence, patch the blocked gates, and verify.",
            Description: "A release checklist repair that rewards batching independent read-only evidence before mutation.",
            Difficulty: "Moderate",
            EstimatedSteps: 10)
    ];

    public IReadOnlyList<WorkbenchScenarioDescriptor> ListScenarios() => _descriptors;

    public WorkbenchScenario Load(string scenarioId)
    {
        var descriptor = _descriptors.FirstOrDefault(item =>
            string.Equals(item.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new InvalidOperationException($"Unknown WorkbenchQuest scenario '{scenarioId}'.");
        }

        return descriptor.ScenarioId switch
        {
            "broken_check" => CreateBrokenCheck(descriptor),
            "missing_mapping" => CreateMissingMapping(descriptor),
            "structured_doc_merge" => CreateStructuredDocMerge(descriptor),
            "word_ladder" => CreateWordLadder(descriptor),
            "release_gate" => CreateReleaseGate(descriptor),
            _ => throw new InvalidOperationException($"WorkbenchQuest scenario '{scenarioId}' is not implemented.")
        };
    }

    private static WorkbenchScenario CreateBrokenCheck(WorkbenchScenarioDescriptor descriptor)
    {
        var files = new Dictionary<string, WorkbenchFile>(StringComparer.Ordinal)
        {
            ["README.md"] = new(
                "README.md",
                """
                # Calculator Workbench

                The calculator exposes a tiny text implementation used by the validation check.
                Keep behavior aligned with the tests. Patch only files in this scenario.
                """,
                ReadOnly: true),
            ["src/Calculator.txt"] = new(
                "src/Calculator.txt",
                """
                module Calculator

                function Add(left, right)
                    return left - right
                end

                function Multiply(left, right)
                    return left * right
                end
                """),
            ["tests/CalculatorTests.txt"] = new(
                "tests/CalculatorTests.txt",
                """
                test Add combines two values
                    expect Calculator.Add(2, 3) == 5
                end

                test Multiply combines repeated values
                    expect Calculator.Multiply(2, 3) == 6
                end
                """,
                ReadOnly: true)
        };

        return new WorkbenchScenario(
            descriptor,
            files,
            RelevantPaths: ["src/Calculator.txt", "tests/CalculatorTests.txt"]);
    }

    private static WorkbenchScenario CreateMissingMapping(WorkbenchScenarioDescriptor descriptor)
    {
        var files = new Dictionary<string, WorkbenchFile>(StringComparer.Ordinal)
        {
            ["README.md"] = new(
                "README.md",
                """
                # Fulfillment Mapping Workbench

                The pipeline joins orders with shipping method mappings and emits normalized labels.
                Patch only the mapping file. The checker compares generated output to expected output.
                """,
                ReadOnly: true),
            ["input/orders.csv"] = new(
                "input/orders.csv",
                """
                order_id,method_code
                1001,STD
                1002,EXP
                1003,ECO
                """,
                ReadOnly: true),
            ["config/mapping.csv"] = new(
                "config/mapping.csv",
                """
                method_code,label
                STD,STANDARD
                EXP,UNKNOWN
                ECO,ECONOMY
                """),
            ["expected/output.csv"] = new(
                "expected/output.csv",
                """
                order_id,shipping_label
                1001,STANDARD
                1002,EXPRESS
                1003,ECONOMY
                """,
                ReadOnly: true)
        };

        return new WorkbenchScenario(
            descriptor,
            files,
            RelevantPaths: ["config/mapping.csv", "expected/output.csv", "input/orders.csv"]);
    }

    private static WorkbenchScenario CreateStructuredDocMerge(WorkbenchScenarioDescriptor descriptor)
    {
        var files = new Dictionary<string, WorkbenchFile>(StringComparer.Ordinal)
        {
            ["merge_rules.txt"] = new(
                "merge_rules.txt",
                """
                Merge rules:
                1. Preserve section order from base.md.
                2. Use revision_a.md for section intro.
                3. Use revision_b.md for section billing.
                4. Include section support from revision_b.md after billing.
                5. Drop section legacy if any revision marks it deleted.
                6. merged.md must contain each kept section id exactly once.
                """,
                ReadOnly: true),
            ["base.md"] = new(
                "base.md",
                """
                <!-- section:id=intro -->
                Welcome to the account guide.

                <!-- section:id=billing -->
                Billing happens on the first day of each month.

                <!-- section:id=legacy -->
                Legacy tokens may be used for old integrations.
                """,
                ReadOnly: true),
            ["revision_a.md"] = new(
                "revision_a.md",
                """
                <!-- section:id=intro -->
                Welcome to the account guide. Keep your profile email current.

                <!-- section:id=billing -->
                Billing happens on the first day of each month.

                <!-- section:id=legacy deleted=true -->
                Legacy tokens may be used for old integrations.
                """,
                ReadOnly: true),
            ["revision_b.md"] = new(
                "revision_b.md",
                """
                <!-- section:id=intro -->
                Welcome to the account guide.

                <!-- section:id=billing -->
                Billing happens on the first business day of each month.

                <!-- section:id=support -->
                Contact support within 30 days to dispute an invoice.
                """,
                ReadOnly: true),
            ["merged.md"] = new(
                "merged.md",
                """
                <!-- section:id=intro -->
                Welcome to the account guide.

                <!-- section:id=billing -->
                Billing happens on the first day of each month.

                <!-- section:id=legacy -->
                Legacy tokens may be used for old integrations.
                """)
        };

        return new WorkbenchScenario(
            descriptor,
            files,
            RelevantPaths: ["merge_rules.txt", "revision_a.md", "revision_b.md", "merged.md"]);
    }

    private static WorkbenchScenario CreateWordLadder(WorkbenchScenarioDescriptor descriptor)
    {
        var files = new Dictionary<string, WorkbenchFile>(StringComparer.Ordinal)
        {
            ["rules.txt"] = new(
                "rules.txt",
                """
                Build a word ladder from cold to warm.
                Rules:
                - Use lowercase four-letter words only.
                - The first word must be cold.
                - The last word must be warm.
                - Each neighboring pair must differ by exactly one letter.
                - Every word must appear in dictionary.txt.
                - Write the ladder as JSON in answer.json.
                """,
                ReadOnly: true),
            ["dictionary.txt"] = new(
                "dictionary.txt",
                """
                cold
                cord
                card
                ward
                warm
                sold
                told
                toll
                tall
                tail
                wail
                wall
                """,
                ReadOnly: true),
            ["answer.json"] = new(
                "answer.json",
                """
                {
                  "ladder": ["cold", "warm"]
                }
                """)
        };

        return new WorkbenchScenario(
            descriptor,
            files,
            RelevantPaths: ["rules.txt", "dictionary.txt", "answer.json"]);
    }

    private static WorkbenchScenario CreateReleaseGate(WorkbenchScenarioDescriptor descriptor)
    {
        var files = new Dictionary<string, WorkbenchFile>(StringComparer.Ordinal)
        {
            ["release/requirements.txt"] = new(
                "release/requirements.txt",
                """
                Release gate requirements:
                - frontend must target /prod
                - backend health route must return 200
                - manifest must include CHANGELOG.md
                """,
                ReadOnly: true),
            ["services/frontend.env"] = new(
                "services/frontend.env",
                """
                APP_NAME=Portal
                API_BASE=/staging
                """),
            ["services/backend.routes"] = new(
                "services/backend.routes",
                """
                GET /health -> 404
                GET /ready -> 200
                """),
            ["release/manifest.txt"] = new(
                "release/manifest.txt",
                """
                README.md: included
                CHANGELOG.md: missing
                LICENSE.txt: included
                """)
        };

        return new WorkbenchScenario(
            descriptor,
            files,
            RelevantPaths:
            [
                "release/requirements.txt",
                "services/frontend.env",
                "services/backend.routes",
                "release/manifest.txt"
            ]);
    }
}
