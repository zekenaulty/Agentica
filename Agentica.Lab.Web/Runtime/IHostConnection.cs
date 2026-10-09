using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

public interface IHostConnection
{
    string ConnectionId { get; }
    Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default);
    void TrySendProgress(ServiceMessage message) { }
}
