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
using LabChatImageEffectJournal = AgenticaLab::ChatImageEffectJournal;
using LabChatImageToolResultPreflight = AgenticaLab::ChatImageToolResultPreflight;
using LabChatPersona = AgenticaLab::ChatPersona;
using LabChatProviderResponseCompiler = AgenticaLab::ChatProviderResponseCompiler;
using LabChatProviderResponseValidationException = AgenticaLab::ChatProviderResponseValidationException;
using LabChatStore = AgenticaLab::ChatStore;
using LabChatToolIds = AgenticaLab::ChatToolIds;
using LabIChatImageStagingWriter = AgenticaLab::IChatImageStagingWriter;
using LabWorkspaceFileReadTool = AgenticaLab::WorkspaceFileReadTool;
using LabWorkspaceFileSearchTool = AgenticaLab::WorkspaceFileSearchTool;
using LabWorkspaceImageCreateTool = AgenticaLab::WorkspaceImageCreateTool;
using LabWorkspaceImageGenerateTool = AgenticaLab::WorkspaceImageGenerateTool;

namespace Agentica.Tests;

public sealed class ChatWorkspaceBoundaryTests
{
    [Fact]
    public void Image_effect_journal_caps_entries_details_and_residual_names()
    {
        var journal = new LabChatImageEffectJournal();
        for (var index = 0; index < 100; index++)
        {
            journal.MutationAttempted($"effect_{index:D3}", new string('x', 500));
        }

        var snapshot = journal.Snapshot("failed");
        var entries = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(snapshot["entries"]);
        var residual = Assert.IsAssignableFrom<IEnumerable<string>>(snapshot["residualOrIndeterminateLocalEffects"]);

        Assert.Equal(64, entries.Count());
        Assert.Equal(36, Convert.ToInt32(snapshot["droppedEntryCount"]));
        Assert.Equal(16, residual.Count());
        Assert.All(entries, entry => Assert.True(entry["detail"]?.ToString()?.Length <= 240));
    }

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
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Combine(fixture.WorkspaceRoot, "images")),
            path => Path.GetFileName(path).StartsWith(".agentica-staging-", StringComparison.Ordinal));
        var journal = EffectJournal(result);
        var entries = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(journal["entries"]);
        Assert.Contains(
            entries,
            entry => entry["category"]?.ToString() == "publish" && entry["outcome"]?.ToString() == "completed");
        Assert.Contains(
            entries,
            entry =>
                entry["category"]?.ToString() == "cleanup" &&
                entry["subject"]?.ToString() == "image_staging_directory" &&
                entry["outcome"]?.ToString() == "completed");
        Assert.DoesNotContain(
            Assert.IsAssignableFrom<IEnumerable<string>>(journal["committedLocalEffects"]),
            effect => effect.StartsWith("staged_", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutsideRoot));
    }

    [Fact]
    public async Task Image_create_cleans_prompt_staging_and_does_not_report_it_as_committed()
    {
        using var fixture = new WorkspaceFixture();
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
                new Dictionary<string, object?> { ["request"] = "Draw a complete staged image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        var journal = EffectJournal(result);
        var entries = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(journal["entries"]);
        Assert.Contains(
            entries,
            entry =>
                entry["category"]?.ToString() == "cleanup" &&
                entry["subject"]?.ToString() == "artist_prompt_staging_directory" &&
                entry["outcome"]?.ToString() == "completed");
        Assert.DoesNotContain(
            Assert.IsAssignableFrom<IEnumerable<string>>(journal["committedLocalEffects"]),
            effect => effect.Contains("staging", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(
                Path.Combine(fixture.WorkspaceRoot, "images"),
                ".agentica-staging-*",
                SearchOption.AllDirectories),
            _ => true);
    }

    [Fact]
    public async Task Image_generate_reports_failed_effect_truth_when_hostile_provider_swaps_output_boundary()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient(
            () => fixture.CreateDirectoryLink("images", fixture.OutsideRoot));
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a hostile-boundary test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(1, imageClient.CallCount);
        var journal = EffectJournal(result);
        Assert.Equal(1, Convert.ToInt32(journal["providerDispatchAttempts"]));
        Assert.Equal(1, Convert.ToInt32(journal["providerResponses"]));
        Assert.Equal("provider_completed_local_compensated", journal["effectState"]);
        Assert.True(Assert.IsType<bool>(journal["cleanupComplete"]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutsideRoot));
    }

    [Fact]
    public async Task Image_generate_compensates_files_and_reports_failed_when_context_store_rejects_insert()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient();
        var (store, conversation, _) = fixture.CreateChatContext();
        fixture.FailImageContextInserts();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a store-failure test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(1, imageClient.CallCount);
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        var journal = EffectJournal(result);
        Assert.True(Assert.IsType<bool>(journal["cleanupComplete"]));
        Assert.True(Convert.ToInt32(journal["cleanupAttempts"]) >= 3);
        Assert.Contains("SqliteException", JsonSerializer.Serialize(journal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_generate_metadata_staging_failure_publishes_no_final_output()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient();
        var stagingWriter = new FailOnCallStagingWriter(failOnCall: 2);
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient,
            stagingWriter);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a staging-failure test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(1, imageClient.CallCount);
        Assert.Equal(2, stagingWriter.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        var journal = EffectJournal(result);
        var entries = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(journal["entries"]);
        Assert.Contains(
            entries,
            entry =>
                entry["subject"]?.ToString() == "staged_image_metadata_file" &&
                entry["outcome"]?.ToString() == "failed_indeterminate");
        Assert.DoesNotContain(entries, entry => entry["category"]?.ToString() == "publish");
        Assert.True(Assert.IsType<bool>(journal["cleanupComplete"]));
    }

    [Fact]
    public async Task Image_provider_response_is_frozen_before_retained_references_can_mutate()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RetainedMutableImageClient();
        var stagingWriter = new CallbackStagingWriter(imageClient.MutateRetainedResponse);
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient,
            stagingWriter);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a frozen-response test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Single(
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(result.Receipt.Data["images"])
                .Cast<object>());
        var imagePath = Directory.GetFiles(Path.Combine(fixture.WorkspaceRoot, "images"), "*.png").Single();
        Assert.Equal(RetainedMutableImageClient.OriginalBytes, await File.ReadAllBytesAsync(imagePath));
    }

    [Fact]
    public async Task Oversized_provider_response_fails_before_local_mutation_and_preserves_request_id()
    {
        using var fixture = new WorkspaceFixture();
        const string responseId = "provider-response-oversized-001";
        var imageClient = new RecordingImageClient(
            responseFactory: request => new ImageGenerationResponse(
                "fake-image-provider",
                request.ModelId,
                [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
                Text: new string('x', 65 * 1024),
                Metadata: new Dictionary<string, string> { ["responseId"] = responseId }));
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw an oversized-response test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        var journal = EffectJournal(result);
        var requestIds = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            journal["providerRequestIds"]);
        Assert.Equal(responseId, requestIds["image_generator"]);
    }

    [Fact]
    public async Task Valid_long_provider_request_id_remains_exact_in_final_effect_evidence()
    {
        using var fixture = new WorkspaceFixture();
        var responseId = $"provider-response-{new string('r', 512)}";
        var imageClient = new RecordingImageClient(
            responseFactory: request => new ImageGenerationResponse(
                "fake-image-provider",
                request.ModelId,
                [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
                Metadata: new Dictionary<string, string> { ["responseId"] = responseId }));
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a long-request-id proof image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        var requestIds = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            EffectJournal(result)["providerRequestIds"]);
        Assert.Equal(responseId, requestIds["image_generator"]);
    }

    [Fact]
    public async Task Oversized_provider_request_id_fails_with_bounded_explicit_effect_evidence()
    {
        using var fixture = new WorkspaceFixture();
        var oversizedResponseId = new string('r', 4097);
        var imageClient = new RecordingImageClient(
            responseFactory: request => new ImageGenerationResponse(
                "fake-image-provider",
                request.ModelId,
                [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
                Metadata: new Dictionary<string, string> { ["responseId"] = oversizedResponseId }));
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw an oversized-request-id proof image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        var requestIds = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            EffectJournal(result)["providerRequestIds"]);
        Assert.Equal("oversized-provider-request-id", requestIds["image_generator"]);
        Assert.DoesNotContain(oversizedResponseId, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Utf8_oversized_provider_request_id_fails_with_bounded_explicit_effect_evidence()
    {
        using var fixture = new WorkspaceFixture();
        var oversizedResponseId = new string('\u754c', 1400);
        var imageClient = new RecordingImageClient(
            responseFactory: request => new ImageGenerationResponse(
                "fake-image-provider",
                request.ModelId,
                [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
                Metadata: new Dictionary<string, string> { ["responseId"] = oversizedResponseId }));
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a UTF-8 request-id proof image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        var requestIds = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            EffectJournal(result)["providerRequestIds"]);
        Assert.Equal("oversized-provider-request-id", requestIds["image_generator"]);
        Assert.DoesNotContain(oversizedResponseId, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_response_metadata_enumeration_is_bounded_independently_of_count()
    {
        const string responseId = "dishonest-metadata-response-id";
        var metadata = new DishonestMetadata(responseId, yieldedEntries: 20_000);
        var response = new ImageGenerationResponse(
            "fake-image-provider",
            "fake-image-model",
            [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
            Metadata: metadata);

        var exception = Assert.Throws<LabChatProviderResponseValidationException>(
            () => LabChatProviderResponseCompiler.Compile(response));

        Assert.Equal(responseId, exception.ProviderRequestId);
        Assert.Equal(0, metadata.CountReads);
        Assert.InRange(metadata.EnumeratedEntries, 1, 34);
    }

    [Fact]
    public void Provider_response_rejects_huge_metadata_key_before_collecting_or_sorting_raw_entries()
    {
        var metadata = new HugeKeyMetadata(yieldedEntries: 32);
        var response = new ImageGenerationResponse(
            "fake-image-provider",
            "fake-image-model",
            [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
            Metadata: metadata);

        _ = Assert.Throws<LabChatProviderResponseValidationException>(
            () => LabChatProviderResponseCompiler.Compile(response));

        Assert.Equal(0, metadata.CountReads);
        Assert.InRange(metadata.EnumeratedEntries, 1, 33);
    }

    [Fact]
    public void Provider_response_image_enumeration_is_bounded_independently_of_count()
    {
        var images = new DishonestImageList(yieldedImages: 20_000);
        var response = new ImageGenerationResponse(
            "fake-image-provider",
            "fake-image-model",
            images);

        _ = Assert.Throws<LabChatProviderResponseValidationException>(
            () => LabChatProviderResponseCompiler.Compile(response));

        Assert.Equal(0, images.CountReads);
        Assert.Equal(0, images.IndexerReads);
        Assert.InRange(images.EnumeratedImages, 1, 5);
    }

    [Fact]
    public void Provider_compiler_rejects_huge_padded_required_identity_with_bounded_evidence()
    {
        var paddedIdentity = $" {new string('p', 1024 * 1024)} ";
        var response = new ImageGenerationResponse(
            paddedIdentity,
            "fake-image-model",
            [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")]);

        var exception = Assert.Throws<LabChatProviderResponseValidationException>(
            () => LabChatProviderResponseCompiler.Compile(response));

        Assert.Equal("unknown", exception.ProviderName);
        Assert.Equal("fake-image-model", exception.ModelId);
        Assert.DoesNotContain(paddedIdentity, exception.Message, StringComparison.Ordinal);
        Assert.InRange(exception.Message.Length, 1, 240);
    }

    [Fact]
    public async Task Huge_padded_provider_exception_message_produces_bounded_failure_receipt()
    {
        using var fixture = new WorkspaceFixture();
        var hugeMessage = $" {new string('x', 1024 * 1024)} ";
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            new ThrowingImageClient(new InvalidOperationException(hugeMessage)));

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a bounded-error proof image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.InRange(result.Receipt.Message.Length, 1, 360);
        Assert.DoesNotContain(hugeMessage, result.Receipt.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hugeMessage, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_would_be_result_is_rejected_before_publication_or_sqlite()
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
                new Dictionary<string, object?> { ["prompt"] = new string('p', (256 * 1024) + 1) }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        Assert.Contains("runtime's per-string byte contract", result.Receipt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_length_staging_corruption_fails_digest_validation_without_publication()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient();
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient,
            new SameLengthCorruptingWriter());

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a digest-validation test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        Assert.Contains("digest", result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Staging_writer_cannot_mutate_supplied_memory_without_digest_failure()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient();
        var stagingWriter = new SuppliedMemoryMutatingWriter();
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient,
            stagingWriter);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a hostile-writer test image." }),
            CancellationToken.None);

        Assert.True(stagingWriter.MutatedSuppliedMemory);
        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        Assert.Contains("digest", result.Receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Image_create_stages_prompt_plan_before_any_prompt_plan_publication()
    {
        using var fixture = new WorkspaceFixture();
        var imageClient = new RecordingImageClient();
        var stagingWriter = new FailOnCallStagingWriter(failOnCall: 1);
        var (store, conversation, persona) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageCreateTool(
            store,
            conversation,
            persona,
            fixture.WorkspaceRoot,
            new LabChatArtistPromptComposer(new RecordingLlmClient()),
            imageClient,
            stagingWriter);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageCreate,
                new Dictionary<string, object?> { ["request"] = "Draw a staged-prompt-plan test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.Equal(0, imageClient.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        var entries = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(
            EffectJournal(result)["entries"]);
        Assert.Contains(
            entries,
            entry =>
                entry["subject"]?.ToString() == "staged_artist_prompt_plan_file" &&
                entry["outcome"]?.ToString() == "failed_indeterminate");
        Assert.DoesNotContain(
            entries,
            entry =>
                entry["subject"]?.ToString() == "artist_prompt_plan_file" &&
                entry["category"]?.ToString() == "publish");
    }

    [Fact]
    public async Task Image_create_rejects_oversized_composer_response_before_local_mutation()
    {
        using var fixture = new WorkspaceFixture();
        const string responseId = "composer-response-oversized-001";
        var imageClient = new RecordingImageClient();
        var llmClient = new RecordingLlmClient(_ => new LlmResponse(
            "fake-llm-provider",
            "fake-composer-model",
            "{}",
            StructuredJson: new string('x', (128 * 1024) + 1),
            Metadata: new Dictionary<string, string> { ["responseId"] = responseId }));
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
                new Dictionary<string, object?> { ["request"] = "Draw an invalid-composer test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.Equal(0, imageClient.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        var requestIds = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            EffectJournal(result)["providerRequestIds"]);
        Assert.Equal(responseId, requestIds["artist_prompt_composer"]);
    }

    [Fact]
    public void Image_result_preflight_reserves_the_runtime_aggregate_contract()
    {
        var data = new Dictionary<string, object?>
        {
            ["prompt"] = new string('x', (256 * 1024) + 1)
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LabChatImageToolResultPreflight.ValidateAndSerialize(
                data,
                LabChatToolIds.WorkspaceImageGenerate,
                imageCount: 1,
                firstPath: "images/test.png"));

        Assert.Contains("per-string byte contract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_create_compensates_prompt_mutations_when_image_provider_dispatch_fails()
    {
        using var fixture = new WorkspaceFixture();
        var (store, conversation, persona) = fixture.CreateChatContext();
        var updatedAtBefore = Assert.IsType<LabChatConversation>(
            store.GetConversation(conversation.ConversationId)).UpdatedAt;
        var tool = new LabWorkspaceImageCreateTool(
            store,
            conversation,
            persona,
            fixture.WorkspaceRoot,
            new LabChatArtistPromptComposer(new RecordingLlmClient()),
            new ThrowingImageClient(new LlmClientException("hostile-image-provider", "forced failure")));

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageCreate,
                new Dictionary<string, object?> { ["request"] = "Draw a compensation test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        var updatedAtAfter = Assert.IsType<LabChatConversation>(
            store.GetConversation(conversation.ConversationId)).UpdatedAt;
        Assert.Equal(updatedAtBefore, updatedAtAfter);
        var journal = EffectJournal(result);
        Assert.Equal(2, Convert.ToInt32(journal["providerDispatchAttempts"]));
        Assert.Equal(1, Convert.ToInt32(journal["providerResponses"]));
        Assert.True(Assert.IsType<bool>(journal["cleanupComplete"]));
    }

    [Fact]
    public void Public_context_insertion_still_updates_the_conversation_timestamp()
    {
        using var fixture = new WorkspaceFixture();
        var (store, conversation, _) = fixture.CreateChatContext();
        var updatedAtBefore = Assert.IsType<LabChatConversation>(
            store.GetConversation(conversation.ConversationId)).UpdatedAt;

        _ = store.AddContextItem(
            conversation.ConversationId,
            "note",
            "ordinary public context insertion",
            "test");

        var updatedAtAfter = Assert.IsType<LabChatConversation>(
            store.GetConversation(conversation.ConversationId)).UpdatedAt;
        Assert.True(updatedAtAfter > updatedAtBefore);
    }

    [Fact]
    public async Task Image_create_reports_partial_and_residual_context_when_compensation_is_refused()
    {
        using var fixture = new WorkspaceFixture();
        var (store, conversation, persona) = fixture.CreateChatContext();
        fixture.FailContextDeletes();
        var tool = new LabWorkspaceImageCreateTool(
            store,
            conversation,
            persona,
            fixture.WorkspaceRoot,
            new LabChatArtistPromptComposer(new RecordingLlmClient()),
            new ThrowingImageClient(new LlmClientException("hostile-image-provider", "forced failure")));

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageCreate,
                new Dictionary<string, object?> { ["request"] = "Draw a residual-effect test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Partial, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Single(store.GetContextItems(conversation.ConversationId, 10));
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        var journal = EffectJournal(result);
        Assert.False(Assert.IsType<bool>(journal["cleanupComplete"]));
        Assert.Equal("partial_or_indeterminate", journal["effectState"]);
        Assert.Contains(
            "artist_prompt_context_item",
            Assert.IsAssignableFrom<IEnumerable<string>>(journal["residualOrIndeterminateLocalEffects"]));
    }

    [Fact]
    public async Task Image_generate_reports_cancelled_indeterminate_after_provider_dispatch()
    {
        using var fixture = new WorkspaceFixture();
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            new ThrowingImageClient(new OperationCanceledException("forced cancellation")));

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a cancellation test image." }),
            CancellationToken.None);

        Assert.Equal(ReceiptStatus.Cancelled, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        var journal = EffectJournal(result);
        Assert.Equal(1, Convert.ToInt32(journal["providerDispatchAttempts"]));
        Assert.Equal(0, Convert.ToInt32(journal["providerResponses"]));
        Assert.Equal("provider_outcome_indeterminate_local_compensated", journal["effectState"]);
    }

    [Fact]
    public async Task Image_generate_does_not_translate_out_of_memory_into_a_tool_receipt()
    {
        using var fixture = new WorkspaceFixture();
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            new ThrowingImageClient(CreateOutOfMemoryException()));

        await Assert.ThrowsAsync<OutOfMemoryException>(() => tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw an OOM propagation test image." }),
            CancellationToken.None));
    }

    [Fact]
    public async Task Image_generate_does_not_translate_cancellation_wrapping_fatal_provider_failure()
    {
        using var fixture = new WorkspaceFixture();
        var (store, conversation, _) = fixture.CreateChatContext();
        var cancellation = new OperationCanceledException(
            "Provider cancellation wrapper carrying a fatal failure.",
            CreateOutOfMemoryException(),
            CancellationToken.None);
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            new ThrowingImageClient(cancellation));

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a wrapped fatal propagation test." }),
            CancellationToken.None));

        Assert.Same(cancellation, thrown);
        Assert.IsType<OutOfMemoryException>(thrown.InnerException);
        Assert.Empty(store.GetContextItems(conversation.ConversationId, 10));
    }

    private static OutOfMemoryException CreateOutOfMemoryException() =>
        (OutOfMemoryException)(Activator.CreateInstance(
            typeof(OutOfMemoryException),
            "forced fatal failure") ?? throw new InvalidOperationException("Could not create test exception."));

    [Fact]
    public async Task Image_generate_compensates_partial_file_when_cancelled_during_post_provider_write()
    {
        using var fixture = new WorkspaceFixture();
        using var cancellation = new CancellationTokenSource();
        var imageClient = new RecordingImageClient(cancellation.Cancel);
        var (store, conversation, _) = fixture.CreateChatContext();
        var tool = new LabWorkspaceImageGenerateTool(
            store,
            conversation,
            fixture.WorkspaceRoot,
            imageClient);

        var result = await tool.ExecuteAsync(
            Invocation(
                LabChatToolIds.WorkspaceImageGenerate,
                new Dictionary<string, object?> { ["prompt"] = "Draw a cancelled-write test image." }),
            cancellation.Token);

        Assert.Equal(ReceiptStatus.Cancelled, result.Receipt.Status);
        Assert.NotEqual(ReceiptStatus.Refused, result.Receipt.Status);
        Assert.Equal(1, imageClient.CallCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "images")));
        var journal = EffectJournal(result);
        Assert.Equal(1, Convert.ToInt32(journal["providerResponses"]));
        Assert.True(Convert.ToInt32(journal["localMutationAttempts"]) >= 2);
        Assert.True(Convert.ToInt32(journal["cleanupAttempts"]) >= 2);
        Assert.True(Assert.IsType<bool>(journal["cleanupComplete"]));
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

    private static IReadOnlyDictionary<string, object?> EffectJournal(ToolResult result)
    {
        var value = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            result.Receipt.Data["effectJournal"]);
        Assert.True(Convert.ToInt32(value["journalLimit"]) > 0);
        Assert.True(Convert.ToInt32(value["droppedEntryCount"]) >= 0);
        return value;
    }

    private sealed class RecordingImageClient : IImageGenerationClient
    {
        private readonly Action? _afterDispatch;
        private readonly Func<ImageGenerationRequest, ImageGenerationResponse>? _responseFactory;

        public RecordingImageClient(
            Action? afterDispatch = null,
            Func<ImageGenerationRequest, ImageGenerationResponse>? responseFactory = null)
        {
            _afterDispatch = afterDispatch;
            _responseFactory = responseFactory;
        }

        public int CallCount { get; private set; }

        public Task<ImageGenerationResponse> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            _afterDispatch?.Invoke();
            return Task.FromResult(
                _responseFactory?.Invoke(request) ??
                new ImageGenerationResponse(
                    "fake-image-provider",
                    request.ModelId,
                    [new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png")],
                    Text: string.Empty));
        }
    }

    private sealed class RetainedMutableImageClient : IImageGenerationClient
    {
        public static byte[] OriginalBytes { get; } = [0x89, 0x50, 0x4e, 0x47];

        private readonly byte[] _bytes = OriginalBytes.ToArray();
        private readonly List<GeneratedImage> _images;

        public RetainedMutableImageClient()
        {
            _images = [new GeneratedImage(_bytes, "image/png")];
        }

        public Task<ImageGenerationResponse> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImageGenerationResponse(
                "retained-provider",
                request.ModelId,
                _images,
                Metadata: new Dictionary<string, string> { ["responseId"] = "retained-response-001" }));

        public void MutateRetainedResponse()
        {
            Array.Fill(_bytes, (byte)0xff);
            for (var index = 0; index < 8; index++)
            {
                _images.Add(new GeneratedImage([1, 2, 3, 4], "image/png"));
            }
        }
    }

    private sealed class DishonestMetadata(string responseId, int yieldedEntries)
        : IReadOnlyDictionary<string, string>
    {
        public int Count
        {
            get
            {
                CountReads++;
                return 1;
            }
        }

        public int CountReads { get; private set; }

        public int EnumeratedEntries { get; private set; }

        public IEnumerable<string> Keys => throw new InvalidOperationException("Keys must not be read.");

        public IEnumerable<string> Values => throw new InvalidOperationException("Values must not be read.");

        public string this[string key] => throw new InvalidOperationException("Indexer must not be read.");

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            for (var index = 0; index < yieldedEntries; index++)
            {
                EnumeratedEntries++;
                yield return index == 0
                    ? new KeyValuePair<string, string>("responseId", responseId)
                    : new KeyValuePair<string, string>($"key-{index}", $"value-{index}");
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class DishonestImageList(int yieldedImages) : IReadOnlyList<GeneratedImage>
    {
        public int Count
        {
            get
            {
                CountReads++;
                return 1;
            }
        }

        public int CountReads { get; private set; }

        public int IndexerReads { get; private set; }

        public int EnumeratedImages { get; private set; }

        public GeneratedImage this[int index]
        {
            get
            {
                IndexerReads++;
                throw new InvalidOperationException("Indexer must not be read.");
            }
        }

        public IEnumerator<GeneratedImage> GetEnumerator()
        {
            for (var index = 0; index < yieldedImages; index++)
            {
                EnumeratedImages++;
                yield return new GeneratedImage([0x89, 0x50, 0x4e, 0x47], "image/png");
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class HugeKeyMetadata(int yieldedEntries) : IReadOnlyDictionary<string, string>
    {
        private readonly string _commonPrefix = new('k', 1024 * 1024);

        public int Count
        {
            get
            {
                CountReads++;
                return yieldedEntries;
            }
        }

        public int CountReads { get; private set; }

        public int EnumeratedEntries { get; private set; }

        public IEnumerable<string> Keys => throw new InvalidOperationException("Keys must not be read.");

        public IEnumerable<string> Values => throw new InvalidOperationException("Values must not be read.");

        public string this[string key] => throw new InvalidOperationException("Indexer must not be read.");

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            for (var index = 0; index < yieldedEntries; index++)
            {
                EnumeratedEntries++;
                yield return new KeyValuePair<string, string>(
                    $"{_commonPrefix}{index:D2}",
                    $"value-{index}");
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class ThrowingImageClient : IImageGenerationClient
    {
        private readonly Exception _exception;

        public ThrowingImageClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<ImageGenerationResponse> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImageGenerationResponse>(_exception);
    }

    private sealed class FailOnCallStagingWriter : LabIChatImageStagingWriter
    {
        private readonly int _failOnCall;

        public FailOnCallStagingWriter(int failOnCall)
        {
            _failOnCall = failOnCall;
        }

        public int CallCount { get; private set; }

        public async Task WriteAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == _failOnCall)
            {
                throw new IOException("forced staged write failure");
            }

            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private sealed class CallbackStagingWriter : LabIChatImageStagingWriter
    {
        private readonly Action _callback;
        private bool _called;

        public CallbackStagingWriter(Action callback)
        {
            _callback = callback;
        }

        public async Task WriteAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            if (!_called)
            {
                _called = true;
                _callback();
            }

            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private sealed class SameLengthCorruptingWriter : LabIChatImageStagingWriter
    {
        public async Task WriteAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            var corrupted = content.ToArray();
            corrupted[^1] ^= 0xff;
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(corrupted, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private sealed class SuppliedMemoryMutatingWriter : LabIChatImageStagingWriter
    {
        public bool MutatedSuppliedMemory { get; private set; }

        public async Task WriteAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                    content,
                    out ArraySegment<byte> segment) ||
                segment.Array is null ||
                segment.Count == 0)
            {
                throw new InvalidOperationException("Expected array-backed staging content.");
            }

            segment.Array[segment.Offset + segment.Count - 1] ^= 0xff;
            MutatedSuppliedMemory = true;

            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private sealed class RecordingLlmClient : ILlmClient
    {
        private readonly Func<LlmRequest, LlmResponse>? _responseFactory;

        public RecordingLlmClient(Func<LlmRequest, LlmResponse>? responseFactory = null)
        {
            _responseFactory = responseFactory;
        }

        public int CallCount { get; private set; }

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                _responseFactory?.Invoke(request) ??
                new LlmResponse(
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

        public void FailImageContextInserts()
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "chat.sqlite")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                create trigger fail_image_context_insert
                before insert on context_items
                when new.kind = 'image'
                begin
                    select raise(abort, 'forced image context failure');
                end;
                """;
            command.ExecuteNonQuery();
        }

        public void FailContextDeletes()
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "chat.sqlite")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                create trigger fail_context_delete
                before delete on context_items
                begin
                    select raise(abort, 'forced context cleanup failure');
                end;
                """;
            command.ExecuteNonQuery();
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
