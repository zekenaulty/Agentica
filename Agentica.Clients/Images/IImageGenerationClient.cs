namespace Agentica.Clients.Images;

public interface IImageGenerationClient
{
    Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);
}
