using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Core;

namespace AiUsageWidget.Providers
{
    // v0.1: these CLIs are stubs - every fetch answers NotConfigured. Real implementations
    // follow spec section 11: a pure Parse(json) plus I/O in FetchAsync, each verified with
    // one live call before coding, and the token rule of section 7 (read, use, forget).
    public sealed class ClaudeCodeProvider : IUsageProvider
    {
        public string Id { get { return "claude-code"; } }
        public string DisplayName { get { return "Claude Code"; } }
        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            return Task.FromResult(Stub.NotConfigured(Id, DisplayName));
        }
    }

    public sealed class ZaiProvider : IUsageProvider
    {
        public string Id { get { return "zai"; } }
        public string DisplayName { get { return "Z.ai GLM"; } }
        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            return Task.FromResult(Stub.NotConfigured(Id, DisplayName));
        }
    }

    public sealed class MuseProvider : IUsageProvider
    {
        public string Id { get { return "muse"; } }
        public string DisplayName { get { return "Muse"; } }
        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            return Task.FromResult(Stub.NotConfigured(Id, DisplayName));
        }
    }

    public sealed class OpenCodeGoProvider : IUsageProvider
    {
        public string Id { get { return "opencode-go"; } }
        public string DisplayName { get { return "OpenCode Go"; } }
        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            return Task.FromResult(Stub.NotConfigured(Id, DisplayName));
        }
    }

    static class Stub
    {
        public static UsageSnapshot NotConfigured(string id, string displayName)
        {
            var s = new UsageSnapshot();
            s.ProviderId = id;
            s.DisplayName = displayName;
            s.Status = ProviderStatus.NotConfigured;
            s.Message = "not configured";
            return s;
        }
    }
}
