using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // The vendor has no usage API: limits are visible only in the web console and the API key
    // is rejected by every usage route (spec section 12). Replace when one exists.
    public sealed class OpenCodeGoProvider : IUsageProvider
    {
        readonly string authFileOverride;

        public OpenCodeGoProvider() : this(null) { }

        // Self-test hook: point detection at a temp auth file.
        public OpenCodeGoProvider(string authFileOverride)
        {
            this.authFileOverride = authFileOverride;
        }

        public string Id { get { return "opencode-go"; } }
        public string DisplayName { get { return "OpenCode Go"; } }

        // Detected when opencode's auth.json has an "opencode-go" entry — the entry's
        // presence only; no key value is read or kept (spec section 13).
        public bool IsDetected()
        {
            string json = Detection.ReadTextIfExists(authFileOverride ?? DefaultAuthPath());
            if (json == null) return false;
            return Json.GetObject(Json.ParseObject(json), "opencode-go") != null;
        }

        static string DefaultAuthPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "opencode", "auth.json");
        }

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
