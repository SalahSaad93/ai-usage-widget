using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageWidget.Core
{
    // GitHub Releases auto-update. The network part is separated from the pure parts (tag
    // comparison, hash check, file swap) so the self-test can cover those without network.
    // settings.update.repo empty -> updater disabled.
    public sealed class Updater
    {
        const string AssetName = "AiUsageWidget.exe";

        readonly UpdateSettings cfg;
        readonly string runningExePath;
        readonly Version runningVersion;
        readonly string publicKeyXml;

        // 0 idle, 1 checking: a second CheckAsync returns while one is running (start-up
        // check plus a manual "Check for updates" would otherwise both write the same files).
        int checking;

        // SHA-256 expected of the update folder's exe, remembered at verification time so
        // InstallPending can re-verify whatever is on disk right before the swap.
        string pendingSha256;

        public Updater(UpdateSettings cfg, string runningExePath, Version runningVersion, string publicKeyXml = null)
        {
            this.cfg = cfg;
            this.runningExePath = runningExePath;
            this.runningVersion = runningVersion;
            this.publicKeyXml = publicKeyXml ?? UpdateKey.PublicKeyXml;
        }

        public string StatusText { get; private set; }   // "" when nothing to say
        public string PendingVersion { get; private set; }
        public bool PendingInstall { get; private set; }

        // UI hooks, all optional.
        public Action StateChanged;         // tray tooltip / menu refresh
        public Action<string> NotifyUser;   // balloon text
        public Action ExitRequested;        // auto-install done: quit so the new exe takes over

        public bool Enabled
        {
            get { return !string.IsNullOrEmpty(cfg.Repo) && cfg.Mode != "off"; }
        }

        public async Task CheckAsync()
        {
            if (string.IsNullOrEmpty(publicKeyXml))
            {
                SetStatus("updates disabled (unsigned build)");
                return;
            }
            if (!Enabled) return;
            if (Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;   // already running
            try
            {
                string token = null;
                if (cfg.RequiresToken)
                {
                    token = TokenStore.Load();
                    if (string.IsNullOrEmpty(token))
                    {
                        SetStatus("updates: token needed");
                        return;
                    }
                }
                try
                {
                    await CheckCoreAsync(token);
                }
                catch (Exception ex)
                {
                    Log.Error("update check failed", ex);
                    SetStatus("update check failed");
                }
            }
            finally
            {
                Interlocked.Exchange(ref checking, 0);
            }
        }

        // Public repository (no token): the release files are fetched from GitHub's plain
        // download links. The API allows only 60 anonymous requests per hour per IP address,
        // which a shared address exhausts; the download links have no such limit. The
        // version comes from the signed manifest itself, so nothing unsigned is trusted.
        async Task CheckDirectAsync()
        {
            string releases = "https://github.com/" + cfg.Repo + "/releases/";
            HttpResponse metaResp = await Http.GetBytesAsync(
                releases + "latest/download/latest.json", null, CancellationToken.None);
            if (metaResp.Status != 200 || metaResp.Bytes == null || metaResp.Bytes.Length == 0)
            {
                SetStatus("update check failed");
                return;
            }
            byte[] metaBytes = metaResp.Bytes;
            var meta = Json.ParseObject(Encoding.UTF8.GetString(metaBytes));
            string version = NormalizeTag(Json.GetStr(meta, "version"));
            if (!IsNewerVersion(version, runningVersion))
            {
                SetStatus("up to date");
                return;
            }

            if (!Directory.Exists(AppPaths.UpdateDir)) Directory.CreateDirectory(AppPaths.UpdateDir);
            string newExePath = Path.Combine(AppPaths.UpdateDir, AssetName);
            string metaPath = Path.Combine(AppPaths.UpdateDir, "latest.json");
            string signaturePath = Path.Combine(AppPaths.UpdateDir, "latest.json.sig");

            HttpResponse sigResp = await Http.GetBytesAsync(
                releases + "latest/download/latest.json.sig", null, CancellationToken.None);
            string signatureBase64 = sigResp.Status == 200 && sigResp.Bytes != null
                ? Encoding.ASCII.GetString(sigResp.Bytes).Trim() : null;
            if (!VerifySignature(metaBytes, signatureBase64, publicKeyXml))
            {
                FailBadSignature(metaPath, signaturePath, newExePath);
                return;
            }

            // The manifest is signed, so its version and asset name are trusted from here.
            string sha256 = Json.GetStr(meta, "sha256");
            string exeUrl = releases + "download/v" + version + "/" + AssetName;
            HttpResponse exeResp = null;
            if (Json.GetStr(meta, "asset") == AssetName && IsAllowedUrl(exeUrl))
                exeResp = await Http.GetBytesAsync(exeUrl, null, CancellationToken.None);
            if (exeResp == null || exeResp.Status != 200 || !VerifySha256(exeResp.Bytes, sha256))
            {
                TryDelete(newExePath);
                TryDelete(metaPath);
                Log.Error("update failed: download does not match the signed manifest", null);
                SetStatus("update failed");
                return;
            }
            File.WriteAllBytes(metaPath, metaBytes);
            File.WriteAllBytes(signaturePath, sigResp.Bytes);
            File.WriteAllBytes(newExePath, exeResp.Bytes);
            OfferOrInstall(version, sha256);
        }

        // A verified download is ready: install it (auto) or tell the user (notify).
        void OfferOrInstall(string version, string sha256)
        {
            pendingSha256 = sha256;   // remembered for the re-verify in InstallPending
            if (cfg.Mode == "notify")
            {
                PendingVersion = version;
                PendingInstall = true;
                SetStatus("update available: v" + version);
                var notify = NotifyUser;
                if (notify != null) notify("AI Usage Widget v" + version + " is available");
                return;
            }
            InstallPending();
        }

        async Task CheckCoreAsync(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                await CheckDirectAsync();
                return;
            }
            string apiUrl = "https://api.github.com/repos/" + cfg.Repo + "/releases/latest";
            var headers = new List<KeyValuePair<string, string>>();
            headers.Add(new KeyValuePair<string, string>("Accept", "application/vnd.github+json"));
            if (!string.IsNullOrEmpty(token))
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + token));
            HttpResponse releaseResp = await Http.GetTextAsync(apiUrl, headers.ToArray(), CancellationToken.None);
            if (releaseResp.Status != 200)
            {
                SetStatus("update check failed");
                return;
            }
            var release = Json.ParseObject(releaseResp.Text);
            string tag = NormalizeTag(Json.GetStr(release, "tag_name"));
            if (!IsNewerVersion(tag, runningVersion))
            {
                SetStatus("up to date");
                return;
            }

            List<object> assets = Json.GetArray(release, "assets");
            var exeAsset = FindAsset(assets, AssetName);
            var metaAsset = FindAsset(assets, "latest.json");
            var signatureAsset = FindAsset(assets, "latest.json.sig");
            if (exeAsset == null || metaAsset == null || signatureAsset == null)
            {
                FailBadSignature(Path.Combine(AppPaths.UpdateDir, "latest.json"),
                    Path.Combine(AppPaths.UpdateDir, "latest.json.sig"), Path.Combine(AppPaths.UpdateDir, AssetName));
                return;
            }

            if (!Directory.Exists(AppPaths.UpdateDir)) Directory.CreateDirectory(AppPaths.UpdateDir);
            string newExePath = Path.Combine(AppPaths.UpdateDir, AssetName);
            string metaPath = Path.Combine(AppPaths.UpdateDir, "latest.json");
            string signaturePath = Path.Combine(AppPaths.UpdateDir, "latest.json.sig");

            var downloadHeaders = new List<KeyValuePair<string, string>>();
            downloadHeaders.Add(new KeyValuePair<string, string>("Accept", "application/octet-stream"));
            byte[] metaBytes = await DownloadAssetAsync(metaAsset, downloadHeaders, token);
            byte[] signatureBytes = await DownloadAssetAsync(signatureAsset, downloadHeaders, token);
            byte[] exeBytes = await DownloadAssetAsync(exeAsset, downloadHeaders, token);
            if (metaBytes == null || signatureBytes == null || exeBytes == null)
            {
                FailBadSignature(metaPath, signaturePath, newExePath);
                return;
            }
            string signatureBase64 = Encoding.ASCII.GetString(signatureBytes).Trim();
            if (!VerifySignature(metaBytes, signatureBase64, publicKeyXml))
            {
                FailBadSignature(metaPath, signaturePath, newExePath);
                return;
            }
            File.WriteAllBytes(metaPath, metaBytes);
            File.WriteAllBytes(signaturePath, signatureBytes);
            File.WriteAllBytes(newExePath, exeBytes);

            var meta = Json.ParseObject(Encoding.UTF8.GetString(metaBytes));
            string metaVersion = NormalizeTag(Json.GetStr(meta, "version"));
            string sha256 = Json.GetStr(meta, "sha256");
            string assetName = Json.GetStr(meta, "asset");
            bool verified = metaVersion == tag
                && assetName == AssetName
                && VerifySha256(exeBytes, sha256);
            if (!verified)
            {
                // Nothing unverified is ever executed: drop the download.
                TryDelete(newExePath);
                TryDelete(metaPath);
                SetStatus("update failed");
                return;
            }
            OfferOrInstall(tag, sha256);
        }

        void FailBadSignature(string metaPath, string signaturePath, string exePath)
        {
            TryDelete(metaPath);
            TryDelete(signaturePath);
            TryDelete(exePath);
            Log.Error("update failed: bad signature", null);
            SetStatus("update failed: bad signature");
        }

        // Private assets come from the asset's API url (octet-stream); public ones from
        // browser_download_url. Only https on github hosts is requested, and the token is
        // attached only on api.github.com (the redirect to the CDN drops it).
        static async Task<byte[]> DownloadAssetAsync(Dictionary<string, object> asset,
            List<KeyValuePair<string, string>> acceptHeaders, string token)
        {
            string url = Json.GetStr(asset, "url");
            if (string.IsNullOrEmpty(url)) return null;
            if (!IsAllowedUrl(url)) return null;
            Uri uri = new Uri(url);
            var headers = new List<KeyValuePair<string, string>>(acceptHeaders);
            if (uri.Host == "api.github.com" && !string.IsNullOrEmpty(token))
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + token));
            HttpResponse resp = await Http.GetBytesAsync(url, headers.ToArray(), CancellationToken.None);
            if (resp.Status != 200 || resp.Bytes == null || resp.Bytes.Length == 0) return null;
            return resp.Bytes;
        }

        public static bool IsAllowedUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "api.github.com"
                || host == "github.com"
                || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        static Dictionary<string, object> FindAsset(List<object> assets, string name)
        {
            if (assets == null) return null;
            foreach (object o in assets)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                if (Json.GetStr(d, "name") == name) return d;
            }
            return null;
        }

        public void InstallPending()
        {
            string newExePath = Path.Combine(AppPaths.UpdateDir, AssetName);
            int pid = Process.GetCurrentProcess().Id;
            InstallPendingFile(newExePath, delegate(string path)
            {
                var start = new ProcessStartInfo();
                start.FileName = path;
                start.Arguments = "--updated " + pid.ToString(CultureInfo.InvariantCulture);
                start.UseShellExecute = false;
                Process.Start(start);
            });
        }

        // Marks a download as verified and waiting for install (what CheckCoreAsync does
        // after its hash check); internal so the self-test can drive InstallPendingFile
        // without the network.
        internal void MarkPending(string version, string sha256Hex)
        {
            pendingSha256 = sha256Hex;
            PendingVersion = version;
            PendingInstall = true;
        }

        // The install path shared by auto and notify modes. The verified exe may sit in the
        // update folder a long time before the user clicks install, so the file is re-read
        // and re-verified against the remembered hash; on mismatch it is deleted, the
        // pending state cleared and "update failed" reported. Nothing unverified is executed.
        internal bool InstallPendingFile(string newExePath, Action<string> startNew)
        {
            byte[] onDisk = TryReadAllBytes(newExePath);
            if (pendingSha256 == null || !VerifySha256(onDisk, pendingSha256))
            {
                Log.Error("pending update no longer matches the hash verified at download", null);
                TryDelete(newExePath);
                PendingInstall = false;
                PendingVersion = null;
                pendingSha256 = null;
                SetStatus("update failed");
                return false;
            }
            bool swapped = false;
            try
            {
                // Install the bytes that were just verified, not whatever the file holds a
                // moment later: nothing can be swapped in between the check and the install.
                swapped = TrySwapBytes(runningExePath, onDisk, startNew);
                if (swapped) TryDelete(newExePath);
            }
            catch (Exception ex)
            {
                Log.Error("update install failed", ex);
                swapped = false;
            }
            if (swapped)
            {
                PendingInstall = false;
                PendingVersion = null;
                pendingSha256 = null;
                SetStatus("");
                var exit = ExitRequested;
                if (exit != null) exit();
                return true;
            }
            SetStatus("update failed");
            return false;
        }

        // As TrySwap, but the new exe is written from bytes already verified in memory.
        public static bool TrySwapBytes(string runningExePath, byte[] newExe, Action<string> startNew)
        {
            if (newExe == null || newExe.Length == 0) return false;
            string dir = Path.GetDirectoryName(runningExePath);
            string oldPath = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(runningExePath) + ".old" + Path.GetExtension(runningExePath));
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(runningExePath, oldPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            try
            {
                File.WriteAllBytes(runningExePath, newExe);
                startNew(runningExePath);
                return true;
            }
            catch (Exception)
            {
                TryDelete(runningExePath);
                Rollback(oldPath, runningExePath);
                return false;
            }
        }

        // Rename the running exe to <name>.old.exe, move the new one into place, launch it
        // with --updated <pid>. Any failure rolls the rename back and returns false.
        public static bool TrySwap(string runningExePath, string newExePath, Action<string> startNew)
        {
            string dir = Path.GetDirectoryName(runningExePath);
            string oldPath = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(runningExePath) + ".old" + Path.GetExtension(runningExePath));
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(runningExePath, oldPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            try
            {
                File.Move(newExePath, runningExePath);
            }
            catch (IOException)
            {
                Rollback(oldPath, runningExePath);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                Rollback(oldPath, runningExePath);
                return false;
            }
            try
            {
                startNew(runningExePath);
                return true;
            }
            catch (Exception)
            {
                // Put the new exe back where the download left it, then the old one at the
                // running path: a failed launch loses nothing and the install can be retried.
                try
                {
                    File.Move(runningExePath, newExePath);
                }
                catch (IOException)
                {
                    TryDelete(runningExePath);
                }
                catch (UnauthorizedAccessException)
                {
                    TryDelete(runningExePath);
                }
                Rollback(oldPath, runningExePath);
                return false;
            }
        }

        // Runs in the NEW process (argv --updated <oldPid>): wait for the old process to
        // exit, then delete the .old file.
        public static void FinishUpdate(int oldPid)
        {
            string oldPath = AppPaths.OldExePath;
            if (oldPid > 0)
            {
                try
                {
                    using (Process old = Process.GetProcessById(oldPid))
                    {
                        old.WaitForExit(20000);
                    }
                }
                catch (ArgumentException)
                {
                    // already gone
                }
            }
            TryDelete(oldPath);
        }

        // "v0.2.0" -> "0.2.0"
        public static string NormalizeTag(string tag)
        {
            if (tag == null) return "";
            tag = tag.Trim();
            if (tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V')) tag = tag.Substring(1);
            return tag.Trim();
        }

        // Strictly newer only; garbage -> false.
        public static bool IsNewerVersion(string candidateTag, Version running)
        {
            if (running == null) return false;
            Version candidate;
            if (!Version.TryParse(NormalizeTag(candidateTag), out candidate)) return false;
            return candidate.CompareTo(running) > 0;
        }

        public static bool VerifySha256(byte[] data, string expectedHex)
        {
            if (data == null || string.IsNullOrEmpty(expectedHex)) return false;
            string actual;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                actual = sb.ToString();
            }
            return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static bool VerifySignature(byte[] manifestBytes, string signatureBase64, string publicKeyXml)
        {
            if (manifestBytes == null || manifestBytes.Length == 0 || string.IsNullOrEmpty(signatureBase64)
                || string.IsNullOrEmpty(publicKeyXml)) return false;
            try
            {
                if (signatureBase64.Trim() != signatureBase64) return false;
                for (int i = 0; i < signatureBase64.Length; i++)
                    if (!((signatureBase64[i] >= 'A' && signatureBase64[i] <= 'Z')
                        || (signatureBase64[i] >= 'a' && signatureBase64[i] <= 'z')
                        || (signatureBase64[i] >= '0' && signatureBase64[i] <= '9')
                        || signatureBase64[i] == '+' || signatureBase64[i] == '/' || signatureBase64[i] == '=')) return false;
                byte[] signature = Convert.FromBase64String(signatureBase64);
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(manifestBytes, CryptoConfig.MapNameToOID("SHA256"), signature);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void Rollback(string oldPath, string runningExePath)
        {
            try
            {
                if (!File.Exists(runningExePath) && File.Exists(oldPath)) File.Move(oldPath, runningExePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static byte[] TryReadAllBytes(string path)
        {
            try
            {
                return File.ReadAllBytes(path);
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

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        void SetStatus(string text)
        {
            StatusText = text ?? "";
            if (StatusText.Length > 0) Log.Info("update: " + StatusText);   // the tray text alone is easy to miss
            var changed = StateChanged;
            if (changed != null) changed();
        }
    }
}
