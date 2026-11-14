using System;

namespace AiUsageWidget.Core
{
    // One line of disclosure per consent/settings row: what is read locally, who is
    // contacted, and whether the vendor calls this an official API/CLI. Plain data, no
    // behaviour, so the self-test can assert every registered id is covered.
    public sealed class ProviderInfoEntry
    {
        public string Id;
        public string Reads;      // what local login is read, one sentence
        public string Contacts;   // the host contacted, or "runs the official <name> command"
        public bool Official;     // true only for the official OpenRouter / Muse / Antigravity paths
    }

    // Static catalogue behind the consent and settings windows. An id missing from the
    // catalogue falls back to generic text, so future providers show something honest
    // before anyone updates the wording here.
    public static class ProviderInfo
    {
        public static readonly ProviderInfoEntry[] Entries = new ProviderInfoEntry[]
        {
            new ProviderInfoEntry
            {
                Id = "claude-code",
                Reads = "Reads the Claude Code login file .claude\\.credentials.json on this PC.",
                Contacts = "Contacts api.anthropic.com.",
                Official = false
            },
            new ProviderInfoEntry
            {
                Id = "codex",
                Reads = "Reads the Codex login file .codex\\auth.json on this PC.",
                Contacts = "Contacts chatgpt.com.",
                Official = false
            },
            new ProviderInfoEntry
            {
                Id = "zai",
                Reads = "Reads the Z.ai key from OpenCode, the ZAI_API_KEY environment variable or the ZCode config.",
                Contacts = "Contacts api.z.ai.",
                Official = false
            },
            new ProviderInfoEntry
            {
                Id = "cursor",
                Reads = "Reads the Cursor sign-in from its local settings database.",
                Contacts = "Contacts api2.cursor.sh.",
                Official = false
            },
            new ProviderInfoEntry
            {
                Id = "github-copilot",
                Reads = "Reads the Copilot sign-in file github-copilot\\apps.json on this PC.",
                Contacts = "Contacts api.github.com.",
                Official = false
            },
            new ProviderInfoEntry
            {
                Id = "openrouter",
                Reads = "Uses the API key you enter, or the OPENROUTER_API_KEY environment variable; nothing else is read.",
                Contacts = "Contacts openrouter.ai.",
                Official = true
            },
            new ProviderInfoEntry
            {
                Id = "antigravity",
                Reads = "Reads nothing; no login file is read.",
                Contacts = "Runs the official agy command.",
                Official = true
            },
            new ProviderInfoEntry
            {
                Id = "opencode-go",
                Reads = "Reads nothing beyond checking that OpenCode Go is installed.",
                Contacts = "The vendor has no usage API.",
                Official = false
            },
            new ProviderInfoEntry
            {
                Id = "muse",
                Reads = "Reads nothing; no login file is read.",
                Contacts = "Runs the official muse usage command.",
                Official = true
            }
        };

        // Never null: the fallback below covers ids that are registered but not catalogued.
        public static ProviderInfoEntry For(string id)
        {
            if (id != null)
                foreach (ProviderInfoEntry e in Entries)
                    if (e.Id == id) return e;
            return GenericFor(id);
        }

        public static ProviderInfoEntry GenericFor(string id)
        {
            var e = new ProviderInfoEntry();
            e.Id = id;
            e.Reads = "Reads the local sign-in of this tool from this PC.";
            e.Contacts = "Contacts the vendor's usage endpoint.";
            e.Official = false;
            return e;
        }
    }
}
