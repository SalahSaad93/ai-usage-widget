using AiUsageWidget.Providers;

namespace AiUsageWidget.Core
{
    // Every known provider. settings.json picks the shown subset and the order; the array is
    // built once and handed out as-is.
    public static class ProviderRegistry
    {
        // Experimental providers are never auto-shown (SelectShown): the command they run has
        // never been observed and might send a prompt to the model. An explicit
        // { "id": "<id>", "enabled": true } entry still shows one. Cursor and GitHub Copilot
        // join Antigravity: their endpoints are unofficial and unpinned.
        public static readonly string[] ExperimentalIds =
            { "antigravity", "cursor", "github-copilot" };

        static readonly IUsageProvider[] all = new IUsageProvider[]
        {
            new ClaudeCodeProvider(),
            new CodexProvider(),
            new ZaiProvider(),
            new CursorProvider(),
            new CopilotProvider(),
            new OpenRouterProvider(),
            new AntigravityProvider(),
            new OpenCodeGoProvider(),
            new MuseProvider()
        };

        public static IUsageProvider[] All
        {
            get { return all; }
        }
    }
}
