using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageWidget.Core
{
    // DPAPI-protected GitHub update token, scoped to the current user. Never in settings.json,
    // logs or messages.
    public static class TokenStore
    {
        public static void Save(string token)
        {
            byte[] plain = Encoding.UTF8.GetBytes(token ?? "");
            byte[] blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            string dir = Path.GetDirectoryName(AppPaths.TokenPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(AppPaths.TokenPath, blob);
        }

        // null when absent or unreadable; the caller must treat both as "token needed".
        public static string Load()
        {
            try
            {
                byte[] blob = File.ReadAllBytes(AppPaths.TokenPath);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
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

        public static void Clear()
        {
            try
            {
                File.Delete(AppPaths.TokenPath);
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
