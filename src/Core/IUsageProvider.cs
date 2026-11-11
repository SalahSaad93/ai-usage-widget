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

        // Cheap, local, existence-only detection (spec section 13): is this subscription
        // present on this PC? Reads no token value, starts no process, uses no network.
        bool IsDetected();
    }
}
