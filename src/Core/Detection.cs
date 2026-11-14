using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace AiUsageWidget.Core
{
    // Existence-only probing for auto-detection (spec section 13). Every method takes the
    // folders and PATH as parameters so the self-test drives it with temp folders and a fake
    // PATH; nothing here starts a process or touches the network, and reads return text only
    // so a caller can check a key's presence, never keep it.
    public static class Detection
    {
        public static bool FolderExists(string folder)
        {
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
        }

        public static bool FileExists(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public static bool ProfileFolderExists(string profileFolder, params string[] parts)
        {
            return FolderExists(Combine(profileFolder, parts));
        }

        public static bool ProfileFileExists(string profileFolder, params string[] parts)
        {
            return FileExists(Combine(profileFolder, parts));
        }

        public static bool LocalAppDataFolderExists(string localAppDataFolder, params string[] parts)
        {
            return FolderExists(Combine(localAppDataFolder, parts));
        }

        // True when leafName ("muse.exe") sits in one of the PATH's folders. A null or empty
        // PATH finds nothing; empty segments are skipped.
        public static bool OnPath(string pathValue, string leafName)
        {
            if (string.IsNullOrEmpty(pathValue) || string.IsNullOrEmpty(leafName)) return false;
            foreach (string raw in pathValue.Split(';'))
            {
                string dir = raw.Trim();
                if (dir.Length == 0) continue;
                if (File.Exists(Path.Combine(dir, leafName))) return true;
            }
            return false;
        }

        // The PATH as it is now, not as it was at launch: machine PATH and user PATH read
        // fresh from the registry (variables like %USERPROFILE% expanded), then the process
        // PATH. Registry or expansion failures fall back to the process PATH.
        public static string CurrentPath()
        {
            string machine = ReadPathValue(Registry.LocalMachine,
                "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment");
            string user = ReadPathValue(Registry.CurrentUser, "Environment");
            string process = Environment.GetEnvironmentVariable("PATH");

            var segments = new List<string>();
            if (!string.IsNullOrEmpty(machine))
                foreach (string raw in machine.Split(';'))
                {
                    string dir = Expand(raw);
                    if (dir.Length > 0) segments.Add(dir);
                }
            if (!string.IsNullOrEmpty(user))
                foreach (string raw in user.Split(';'))
                {
                    string dir = Expand(raw);
                    if (dir.Length > 0) segments.Add(dir);
                }
            if (string.IsNullOrEmpty(process)) process = "";
            return segments.Count > 0 ? string.Join(";", segments.ToArray()) + ";" + process
                : process;
        }

        static string Expand(string raw)
        {
            string dir = raw.Trim();
            if (dir.Length == 0) return "";
            try
            {
                return Environment.ExpandEnvironmentVariables(dir);
            }
            catch (ArgumentException)
            {
                return dir;   // malformed variable reference: keep the raw segment
            }
        }

        static string ReadPathValue(RegistryKey root, string subKey)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    return key.GetValue("Path") as string;
                }
            }
            catch (System.Security.SecurityException)
            {
                return null;   // any failure falls back to the process PATH
            }
            catch (IOException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // File contents for presence checks only ("does this entry have a key?"). Returns
        // null when the file is missing or unreadable; the caller discards the text after
        // its check and must not log or store it.
        public static string ReadTextIfExists(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        static string Combine(string root, string[] parts)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string current = root;
            if (parts == null) return current;
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                current = Path.Combine(current, part);
            }
            return current;
        }
    }
}
