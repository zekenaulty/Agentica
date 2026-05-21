using Agentica.Clients.Images;
using Agentica.Clients.Llm;
using Google.GenAI;
using Google.GenAI.Types;
using ClientGeneratedImage = Agentica.Clients.Images.GeneratedImage;

namespace Agentica.Clients.Gemini;

public sealed class GeminiImageGenerationClient : IImageGenerationClient
{
    public const string ProviderName = "Gemini";

    private readonly GeminiClientOptions _options;

    public GeminiImageGenerationClient(GeminiClientOptions? options = null)
    {
        _options = options ?? GeminiClientOptions.FromEnvironment(GeminiModelId.FlashImage31Preview);
    }

    public async Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new LlmClientException(
                ProviderName,
                "Image generation prompt is required.",
                errorKind: LlmClientErrorKind.BadRequest,
                errorClass: "missing_prompt");
        }

        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            ? _options.DefaultModelId
            : request.ModelId;

        var client = CreateClient();
        var config = CreateConfig(request);

        try
        {
            var response = await client.Models
                .GenerateContentAsync(
                    model: modelId,
                    contents: request.Prompt,
                    config: config,
                    cancellationToken)
                .ConfigureAwait(false);

            return MapResponse(modelId, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new LlmClientException(
                ProviderName,
                $"Gemini image generation was canceled by the provider before the caller token was canceled: {exception.Message}",
                exception,
                LlmClientErrorKind.Transient,
                attempts: 1,
                errorClass: "operation_canceled");
        }
        catch (LlmClientException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var classification = GeminiExceptionClassifier.Classify(exception);
            throw new LlmClientException(
                ProviderName,
                $"Gemini image generation failed. provider={ProviderName}; errorKind={classification.ErrorKind}; statusCode={classification.StatusCode?.ToString() ?? "none"}; errorClass={classification.ErrorClass}; message={GeminiExceptionClassifier.SafeMessage(exception)}",
                exception,
                classification.ErrorKind,
                classification.StatusCode,
                attempts: 1,
                classification.ErrorClass);
        }
    }

    private Client CreateClient()
    {
        if (_options.UseVertexAi)
        {
            return new Client(
                vertexAI: true,
                project: _options.Project,
                location: _options.Location);
        }

        var apiKey = _options.ApiKey
            ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? System.Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmClientException(
                ProviderName,
                "Gemini image generation requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.",
                errorKind: LlmClientErrorKind.Authentication,
                errorClass: "missing_api_key");
        }

        return new Client(apiKey: apiKey);
    }

    private static GenerateContentConfig CreateConfig(ImageGenerationRequest request)
    {
        var config = new GenerateContentConfig
        {
            ResponseModalities = ["TEXT", "IMAGE"],
            ImageConfig = new ImageConfig
            {
                AspectRatio = EmptyToNull(request.AspectRatio),
                ImageSize = EmptyToNull(request.ImageSize)
            }
        };

        return config;
    }

    private static ImageGenerationResponse MapResponse(
        string modelId,
        GenerateContentResponse response)
    {
        var parts = response.Parts ?? [];
        var images = parts
            .Where(part => part.InlineData?.Data is { Length: > 0 })
            .Select(part => new ClientGeneratedImage(
                part.InlineData!.Data!,
                string.IsNullOrWhiteSpace(part.InlineData.MimeType)
                    ? "image/png"
                    : part.InlineData.MimeType!))
            .ToArray();

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.Concat(
                parts
                    .Where(part => !string.IsNullOrWhiteSpace(part.Text))
                    .Select(part => part.Text));
        }

        return new ImageGenerationResponse(
            ProviderName,
            modelId,
            images,
            text ?? string.Empty,
            Usage: response.UsageMetadata is null
                ? null
                : new LlmUsage(
                    PromptTokens: response.UsageMetadata.PromptTokenCount,
                    OutputTokens: response.UsageMetadata.CandidatesTokenCount,
                    ThinkingTokens: response.UsageMetadata.ThoughtsTokenCount,
                    TotalTokens: response.UsageMetadata.TotalTokenCount),
            Metadata: response.ResponseId is null
                ? null
                : new Dictionary<string, string>
                {
                    ["responseId"] = response.ResponseId
                });
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
