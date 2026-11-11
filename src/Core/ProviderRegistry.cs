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
                    new CodexProvider(),
                    new ClaudeCodeProvider(),
                    new ZaiProvider(),
                    new MuseProvider(),
                    new OpenCodeGoProvider()
                };
            }
        }
    }
}
