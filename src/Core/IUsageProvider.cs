using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Core
{
    public interface IUsageProvider
    {
        string Id { get; }
        string DisplayName { get; }
        // Never throws for expected conditions: returns NotConfigured / AuthRequired / Error
        // snapshots instead. Unexpected exceptions are caught by RefreshService.
        Task<UsageSnapshot> FetchAsync(CancellationToken ct);
    }
}
