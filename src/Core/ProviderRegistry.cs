using AiUsageWidget.Providers;

namespace AiUsageWidget.Core
{
    // Every known provider. settings.json picks the enabled subset and the order.
    public static class ProviderRegistry
    {
        public static IUsageProvider[] All
        {
            get
            {
                return new IUsageProvider[]
                {
                    new ClaudeCodeProvider(),
                    new CodexProvider(),
                    new ZaiProvider(),
                    new AntigravityProvider(),
                    new OpenCodeGoProvider(),
                    new MuseProvider()
                };
            }
        }
    }
}
