extern alias AgenticaLab;

using System.Diagnostics;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Clients.Images;
using Agentica.Clients.Llm;
using Agentica.Tools;
using Microsoft.Data.Sqlite;
using LabChatArtistPromptComposer = AgenticaLab::ChatArtistPromptComposer;
using LabChatConversation = AgenticaLab::ChatConversation;
using LabChatPersona = AgenticaLab::ChatPersona;
using LabChatStore = AgenticaLab::ChatStore;
using LabChatToolIds = AgenticaLab::ChatToolIds;
using LabWorkspaceFileReadTool = AgenticaLab::WorkspaceFileReadTool;
using LabWorkspaceFileSearchTool = AgenticaLab::WorkspaceFileSearchTool;
using LabWorkspaceImageCreateTool = AgenticaLab::WorkspaceImageCreateTool;
using LabWorkspaceImageGenerateTool = AgenticaLab::WorkspaceImageGenerateTool;

namespace Agentica.Tests;

public sealed class ChatWorkspaceBoundaryTests
{
    [Fact]
    public async Task File_read_refuses_directory_link_escape_without_disclosing_secret()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "outside-secret-read-proof";
        await File.WriteAllTextAsync(Path.Combine(fixture.OutsideRoot, "secret.txt"), secret);
        fixture.CreateDirectoryLink("escape", fixture.OutsideRoot);

        var tool = new LabWorkspaceFileReadTool(fixture.WorkspaceRoot);
        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileRead,
                new Dictionary<string, object?> { ["path"] = Path.Combine("escape", "secret.txt") }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Contains("boundary refused", result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_search_refuses_directory_link_escape_without_returning_match()
    {
        using var fixture = new WorkspaceFixture();
        const string secret = "outside-secret-search-proof";
        await File.WriteAllTextAsync(Path.Combine(fixture.OutsideRoot, "secret.txt"), secret);
        fixture.CreateDirectoryLink("escape", fixture.OutsideRoot);

        var tool = new LabWorkspaceFileSearchTool(fixture.WorkspaceRoot);
        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = secret
                }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Contains("boundary refused", result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Image_generate_refuses_images_link_before_provider_call()
    {
        using var fixture = new WorkspaceFixture();
        fixture.CreateDirectoryLink("images", fixture.OutsideRoot);
        var imageClient = new RecordingImageClient();
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a safe local test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(0, imageClient.CallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutsideRoot));
    }

    [Fact]
    public async Task Image_create_refuses_prompt_link_before_composer_or_image_provider_call()
    {
        using var fixture = new WorkspaceFixture();
        Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "images"));
        fixture.CreateDirectoryLink(Path.Combine("images", "prompts"), fixture.OutsideRoot);
        var llmClient = new RecordingLlmClient();
        var imageClient = new RecordingImageClient();
        var (store, conversation, persona) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageCreateTool(
            store,
            conversation,
            persona,
            fixture.WorkspaceRoot,
            new LabChatArtistPromptComposer(llmClient),
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageCreate,
                new Dictionary<string, object?> { ["request"] = "Draw a safe local test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(0, llmClient.CallCount);
        Assert.Equal(0, imageClient.CallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutsideRoot));
    }

    [Fact]
    public async Task Image_generate_writes_normal_outputs_under_workspace()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient();
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a normal local test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal(1, imageClient.CallCount);
        var outputs = Directory.GetFiles(Path.Combine(fixture.WorkspaceRoot, "images"));
        Assert.Equal(2, outputs.Length);
        Assert.All(outputs, output => Assert.True(IsUnder(fixture.WorkspaceRoot, output)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutsideRoot));
    }

    [Fact]
    public async Task Normal_nested_workspace_paths_remain_readable_and_searchable()
    {
        using var fixture = new WorkspaceFixture();
        var nested = Path.Combine(fixture.WorkspaceRoot, "nested", "deeper");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "note.txt");
        await File.WriteAllTextAsync(file, "normal-path-needle");

        var readTool = new LabWorkspaceFileReadTool(fixture.WorkspaceRoot);
        var read = await readTool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileRead,
                new Dictionary<string, object?> { ["path"] = Path.Combine("nested", "deeper", "note.txt") }),
            CancellationToken.None);
        var searchTool = new LabWorkspaceFileSearchTool(fixture.WorkspaceRoot);
        var search = await searchTool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceFileSearch,
                new Dictionary<string, object?>
                {
                    ["pattern"] = "normal-path-needle",
                    ["path"] = "nested"
                }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, read.Receipt.Status);
        Assert.Contains("normal-path-needle", JsonSerializer.Serialize(read), StringComparison.Ordinal);
        Assert.Equal(ReceiptStatus.Succeeded, search.Receipt.Status);
        Assert.Contains("normal-path-needle", JsonSerializer.Serialize(search), StringComparison.Ordinal);
    }

    private static ToolInvocation Invocation(
        string toolId,
        IReadOnlyDictionary<string, object?> input) =>
        new("run_boundary_test", "step_boundary_test", toolId, input);

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class RecordingImageClient : IImageGenerationClient
    {
        public int CallCount { get; private set; }

        public Task<ImageGenerationResponse> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ImageGenerationResponse(
                "fake-image-provider",
                request.ModelId,
                [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
                Text: string.Empty));
        }
    }

    private sealed class RecordingLlmClient : ILlmClient
    {
        public int CallCount { get; private set; }

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LlmResponse(
                "fake-llm-provider",
                request.ModelId,
                "{}",
                StructuredJson: "{\"finalPrompt\":\"safe prompt\"}",
                FinishReason: LlmFinishReason.Stop));
        }
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly List<string> _links = [];
        private bool _disposed;

        public WorkspaceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"agentica-boundary-{Guid.NewGuid():N}");
            WorkspaceRoot = Path.Combine(Root, "workspace");
            OutsideRoot = Path.Combine(Root, "outside");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(OutsideRoot);
        }

        public string Root { get; }

        public string WorkspaceRoot { get; }

        public string OutsideRoot { get; }

        public (LabChatStore Store, LabChatConversation Conversation, LabChatPersona Persona) CreateChatContext()
        {
            var store = new LabChatStore(Path.Combine(Root, "chat.sqlite"));
            store.EnsureCreated();
            return (
                store,
                store.CreateConversation(
                    "Boundary",
                    "plain",
                    WorkspaceRoot,
                    $"conversation_boundary_{Guid.NewGuid():N}"),
                new LabChatPersona("plain", "Plain", "Plain test persona.", "Plain"));
        }

        public void CreateDirectoryLink(string relativeLinkPath, string target)
        {
            var link = Path.Combine(WorkspaceRoot, relativeLinkPath);
            Directory.CreateDirectory(Path.GetDirectoryName(link) ?? WorkspaceRoot);
            _links.Add(link);

            if (OperatingSystem.IsWindows())
            {
                CreateWindowsJunction(link, target);
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }

            Assert.True(Directory.Exists(link), $"Directory link was not created: {link}");
            Assert.True(
                (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0 ||
                !string.IsNullOrWhiteSpace(new DirectoryInfo(link).LinkTarget),
                $"Test path is not a real link or reparse point: {link}");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var linksRemoved = true;
            foreach (var link in _links.OrderByDescending(path => path.Length))
            {
                try
                {
                    if (Directory.Exists(link) || !string.IsNullOrWhiteSpace(new DirectoryInfo(link).LinkTarget))
                    {
                        Directory.Delete(link);
                    }
                }
                catch
                {
                    linksRemoved = false;
                }
            }

            SqliteConnection.ClearAllPools();
            if (linksRemoved && Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void CreateWindowsJunction(string link, string target)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("/d");
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add("mklink");
            process.StartInfo.ArgumentList.Add("/J");
            process.StartInfo.ArgumentList.Add(link);
            process.StartInfo.ArgumentList.Add(target);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"Could not create Windows junction. Exit={process.ExitCode}; stdout={output}; stderr={error}");
        }
    }
}
