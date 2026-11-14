using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageWidget.Core
{
    // Named secrets the user gives the widget itself (for example an OpenRouter API key),
    // protected with DPAPI for the current Windows user. Never in settings.json, logs or
    // messages. Names are fixed identifiers chosen in code, never user input.
    public static class SecretStore
    {
        public static void Save(string name, string value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(value ?? "");
            byte[] blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            string path = AppPaths.SecretPath(name);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, blob);
        }

        // null when absent, empty or unreadable.
        public static string Load(string name)
        {
            try
            {
                byte[] blob = File.ReadAllBytes(AppPaths.SecretPath(name));
                string value = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (IOException)
            {
                return null;
            }
            catch (CryptographicException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static void Clear(string name)
        {
            try
            {
                File.Delete(AppPaths.SecretPath(name));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
