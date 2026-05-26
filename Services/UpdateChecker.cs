using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AgentReadonly.Services
{
    public class UpdateChecker
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/kosciolek/tato-agent-ai/releases/latest";
        private const string TargetAssetName = "agent-readonly-windows-x64.zip";
        private const string TargetManifestName = "agent-readonly-windows-x64.manifest.json";
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            EnsureTls12();
            AppLog.Info("Checking for updates.");

            GithubRelease release = serializer.Deserialize<GithubRelease>(
                await GetStringAsync(LatestReleaseUrl, cancellationToken));
            if (release == null || release.assets == null)
                throw new InvalidOperationException("GitHub latest release response did not contain assets.");

            GithubReleaseAsset zipAsset = FindAsset(release.assets, TargetAssetName);
            if (zipAsset == null || string.IsNullOrWhiteSpace(zipAsset.browser_download_url))
                throw new InvalidOperationException("Latest release is missing " + TargetAssetName + ".");

            GithubReleaseAsset manifestAsset = FindAsset(release.assets, TargetManifestName);
            if (manifestAsset == null || string.IsNullOrWhiteSpace(manifestAsset.browser_download_url))
                throw new InvalidOperationException("Latest release is missing " + TargetManifestName + ".");

            UpdateManifest manifest = serializer.Deserialize<UpdateManifest>(
                await GetStringAsync(manifestAsset.browser_download_url, cancellationToken));
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.commit))
                throw new InvalidOperationException("Update manifest is missing commit.");

            if (string.IsNullOrWhiteSpace(manifest.asset_name))
                manifest.asset_name = TargetAssetName;
            if (string.IsNullOrWhiteSpace(manifest.sha256) && !string.IsNullOrWhiteSpace(zipAsset.digest))
                manifest.sha256 = zipAsset.digest;

            BuildInfo local = BuildInfo.Load();
            string localCommit = local == null ? "" : (local.commit ?? "").Trim();
            string remoteCommit = (manifest.commit ?? "").Trim();
            bool updateAvailable = !string.Equals(localCommit, remoteCommit, StringComparison.OrdinalIgnoreCase);

            AppLog.Info("Update check finished: local_commit=" + localCommit + " remote_commit=" + remoteCommit + " available=" + updateAvailable);
            return new UpdateCheckResult
            {
                IsUpdateAvailable = updateAvailable,
                LocalBuild = local,
                RemoteManifest = manifest,
                ZipDownloadUrl = zipAsset.browser_download_url,
                ReleaseUrl = release.html_url,
                AssetName = TargetAssetName
            };
        }

        private static GithubReleaseAsset FindAsset(List<GithubReleaseAsset> assets, string name)
        {
            foreach (GithubReleaseAsset asset in assets)
            {
                if (asset != null && string.Equals(asset.name, name, StringComparison.OrdinalIgnoreCase))
                    return asset;
            }
            return null;
        }

        private static async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            using (HttpClient client = CreateClient())
            using (HttpResponseMessage response = await client.GetAsync(url, cancellationToken))
            {
                string body = await response.Content.ReadAsStringAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("GET " + url + " failed with HTTP " + (int)response.StatusCode + ": " + AppLog.Truncate(body));
                return body;
            }
        }

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-readonly-updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
        }

        private class GithubRelease
        {
            public string html_url { get; set; }
            public List<GithubReleaseAsset> assets { get; set; }
        }

        private class GithubReleaseAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
        }
    }

    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public BuildInfo LocalBuild { get; set; }
        public UpdateManifest RemoteManifest { get; set; }
        public string ZipDownloadUrl { get; set; }
        public string ReleaseUrl { get; set; }
        public string AssetName { get; set; }
    }

    public class UpdateManifest
    {
        public string commit { get; set; }
        public string built_at_utc { get; set; }
        public string asset_name { get; set; }
        public string sha256 { get; set; }
    }

    public class BuildInfo
    {
        public string commit { get; set; }
        public string built_at_utc { get; set; }
        public string asset_name { get; set; }

        public static BuildInfo Load()
        {
            try
            {
                if (!File.Exists(AppPaths.BuildInfoPath))
                {
                    AppLog.Info("Build info file not found: " + AppPaths.BuildInfoPath);
                    return null;
                }

                string json = File.ReadAllText(AppPaths.BuildInfoPath);
                return new JavaScriptSerializer().Deserialize<BuildInfo>(json);
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to load build info: " + AppPaths.BuildInfoPath, ex);
                return null;
            }
        }
    }
}
