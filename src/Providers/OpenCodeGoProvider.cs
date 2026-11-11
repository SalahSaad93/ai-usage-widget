using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // The vendor has no usage API: limits are visible only in the web console and the API key
    // is rejected by every usage route (spec section 12). Replace when one exists.
    public sealed class OpenCodeGoProvider : IUsageProvider
    {
        public string Id { get { return "opencode-go"; } }
        public string DisplayName { get { return "OpenCode Go"; } }

        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            var s = new UsageSnapshot();
            s.ProviderId = Id;
            s.DisplayName = DisplayName;
            s.Status = ProviderStatus.NotConfigured;
            s.Message = "no usage API from vendor";
            return Task.FromResult(s);
        }
    }
}
