using System;
using System.Collections.Generic;
using System.Text;

namespace AiUsageWidget.Core
{
    public sealed class KnownTool
    {
        public string Id;
        public string DisplayName;
    }

    // AI tools the widget has no usage provider for (spec section 13). Signatures are
    // existence-only; the folders and PATH come in as parameters so the self-test drives
    // detection with temp folders and a fake PATH. Suggestions never become usage rows.
    public static class KnownTools
    {
        public static List<KnownTool> All()
        {
            var all = new List<KnownTool>();
            all.Add(new KnownTool { Id = "cursor", DisplayName = "Cursor" });
            all.Add(new KnownTool { Id = "github-copilot", DisplayName = "GitHub Copilot" });
            all.Add(new KnownTool { Id = "gemini-cli", DisplayName = "Gemini CLI" });
            all.Add(new KnownTool { Id = "grok-build", DisplayName = "Grok Build" });
            all.Add(new KnownTool { Id = "antigravity", DisplayName = "Antigravity" });
            return all;
        }

        public static bool IsDetected(KnownTool tool, string profileFolder,
            string localAppDataFolder, string pathValue)
        {
            if (tool == null) return false;
            switch (tool.Id)
            {
                case "cursor":
                    return Detection.ProfileFolderExists(profileFolder, ".cursor")
                        || Detection.OnPath(pathValue, "cursor-agent.exe")
                        || Detection.OnPath(pathValue, "cursor-agent.cmd")
                        || Detection.OnPath(pathValue, "cursor-agent.bat");
                case "github-copilot":
                    return Detection.LocalAppDataFolderExists(localAppDataFolder, "github-copilot")
                        || Detection.ProfileFolderExists(profileFolder, ".config", "github-copilot");
                case "gemini-cli":
                    // The bare .gemini folder is also created by Antigravity (spec section 13),
                    // so only the OAuth creds file or a PATH hit counts.
                    return Detection.ProfileFileExists(profileFolder, ".gemini", "oauth_creds.json")
                        || Detection.OnPath(pathValue, "gemini.exe")
                        || Detection.OnPath(pathValue, "gemini.cmd")
                        || Detection.OnPath(pathValue, "gemini.bat");
                case "grok-build":
                    return Detection.ProfileFolderExists(profileFolder, ".grok")
                        || Detection.OnPath(pathValue, "grok.exe")
                        || Detection.OnPath(pathValue, "grok.cmd")
                        || Detection.OnPath(pathValue, "grok.bat");
                case "antigravity":
                    return Detection.ProfileFolderExists(profileFolder, ".antigravity")
                        || Detection.OnPath(pathValue, "agy.exe")
                        || Detection.OnPath(pathValue, "agy.cmd")
                        || Detection.OnPath(pathValue, "agy.bat");
                default:
                    return false;
            }
        }

        public static string DisplayNameOf(string toolId)
        {
            foreach (KnownTool t in All())
                if (t.Id == toolId) return t.DisplayName;
            return toolId;
        }

        // Detected tool ids minus the dismissed ones — the suggestions. coveredIds are ids a
        // real provider now serves (spec section 14: the antigravity provider suppresses the
        // Antigravity suggestion once the agy CLI is detected).
        public static List<string> SuggestedIds(ICollection<string> detectedIds,
            ICollection<string> dismissedIds, ICollection<string> coveredIds = null)
        {
            var list = new List<string>();
            if (detectedIds == null) return list;
            foreach (string id in detectedIds)
            {
                if (coveredIds != null && coveredIds.Contains(id)) continue;
                if (dismissedIds == null || !dismissedIds.Contains(id)) list.Add(id);
            }
            return list;
        }

        // Extra tray help line for a tool whose desktop app exists but whose provider is not
        // usable yet (spec section 14). Null means the tool gets no help line. Cursor and
        // GitHub Copilot keep their suggestions (they say the tool was found); the line
        // points at the Subscriptions switch their experimental providers live behind.
        public static string HelpText(string toolId)
        {
            if (toolId == "cursor" || toolId == "github-copilot")
                return "Experimental: enable under Subscriptions";
            if (toolId == "antigravity") return "Install the agy CLI and sign in to see usage";
            return null;
        }

        // "Request support…" target for the tray submenu. An empty repo disables the item
        // (returns null). The tool name is URL-encoded: spaces become '+', reserved
        // characters are percent-escaped, so the browser gets one well-formed argument.
        public static string IssueUrl(string repo, string toolName)
        {
            if (string.IsNullOrEmpty(repo)) return null;
            string title = EncodeTitle("Add provider: " + toolName);
            return "https://github.com/" + repo + "/issues/new?title=" + title;
        }

        static string EncodeTitle(string title)
        {
            var sb = new StringBuilder();
            foreach (char c in title)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '.' || c == '_' || c == '~' || c == ':' || c == '+')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('+');
                else
                    sb.Append(Uri.HexEscape(c));
            }
            return sb.ToString();
        }
    }
}
