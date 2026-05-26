using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentReadonly.Services
{
    public class UpdateInstaller
    {
        public async Task StageAndLaunchAsync(UpdateCheckResult update, int currentProcessId, CancellationToken cancellationToken)
        {
            if (update == null || update.RemoteManifest == null)
                throw new ArgumentNullException("update");
            if (string.IsNullOrWhiteSpace(update.ZipDownloadUrl))
                throw new InvalidOperationException("Update download URL is missing.");

            EnsureInstallDirectoryWritable();

            string updateDir = Path.Combine(AppPaths.UpdatesDirectory, DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(updateDir);

            string zipPath = Path.Combine(updateDir, update.AssetName ?? "agent-readonly-windows-x64.zip");
            string scriptPath = Path.Combine(updateDir, "apply-update.ps1");
            string extractDir = Path.Combine(updateDir, "extract");

            AppLog.Info("Downloading update: url=" + update.ZipDownloadUrl + " path=" + zipPath);
            await DownloadFileAsync(update.ZipDownloadUrl, zipPath, cancellationToken);
            VerifySha256(zipPath, update.RemoteManifest.sha256);

            File.WriteAllText(scriptPath, BuildUpdateScript(), Encoding.UTF8);
            LaunchUpdateScript(scriptPath, currentProcessId, zipPath, AppPaths.ExecutableDirectory, Process.GetCurrentProcess().MainModule.FileName, extractDir);
            AppLog.Info("Update script launched: " + scriptPath);
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-readonly-updater");
                using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Update download failed with HTTP " + (int)response.StatusCode + ".");

                    using (Stream source = await response.Content.ReadAsStreamAsync())
                    using (FileStream destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await source.CopyToAsync(destination, 81920, cancellationToken);
                    }
                }
            }
        }

        private static void VerifySha256(string path, string expected)
        {
            string normalizedExpected = NormalizeSha256(expected);
            if (string.IsNullOrWhiteSpace(normalizedExpected))
                throw new InvalidOperationException("Update manifest is missing SHA-256.");

            string actual = ComputeSha256(path);
            if (!string.Equals(actual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Update SHA-256 mismatch. Expected " + normalizedExpected + " but got " + actual + ".");

            AppLog.Info("Update SHA-256 verified: " + actual);
        }

        private static string NormalizeSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            value = value.Trim();
            const string prefix = "sha256:";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(prefix.Length);
            return value.Trim();
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        private static void EnsureInstallDirectoryWritable()
        {
            string testPath = Path.Combine(AppPaths.ExecutableDirectory, ".update-write-test");
            try
            {
                File.WriteAllText(testPath, "ok");
                File.Delete(testPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The app folder is not writable. Move the app to a writable folder or update it manually.", ex);
            }
        }

        private static void LaunchUpdateScript(string scriptPath, int currentProcessId, string zipPath, string installDir, string exePath, string extractDir)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "powershell.exe";
            startInfo.Arguments =
                "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
                " -ProcessId " + currentProcessId.ToString() +
                " -ZipPath " + Quote(zipPath) +
                " -InstallDir " + Quote(installDir) +
                " -ExePath " + Quote(exePath) +
                " -ExtractDir " + Quote(extractDir) +
                " -LogPath " + Quote(AppPaths.LogPath);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process.Start(startInfo);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string BuildUpdateScript()
        {
            return @"
param(
    [int]$ProcessId,
    [string]$ZipPath,
    [string]$InstallDir,
    [string]$ExePath,
    [string]$ExtractDir,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

function Write-UpdateLog($Message) {
    try {
        $dir = Split-Path -Parent $LogPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        Add-Content -LiteralPath $LogPath -Value ((Get-Date).ToString('o') + ' [INFO] updater: ' + $Message)
    } catch {
    }
}

try {
    Write-UpdateLog 'Waiting for app process to exit.'
    Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300

    if (Test-Path -LiteralPath $ExtractDir) {
        Remove-Item -LiteralPath $ExtractDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $ExtractDir)

    Get-ChildItem -LiteralPath $ExtractDir -Force | ForEach-Object {
        $target = Join-Path $InstallDir $_.Name
        if ($_.PSIsContainer) {
            if (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force
            }
            Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force
        } else {
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }

    Write-UpdateLog 'Update copied. Relaunching app.'
    Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir
} catch {
    try {
        Add-Content -LiteralPath $LogPath -Value ((Get-Date).ToString('o') + ' [ERROR] updater: ' + ($_ | Out-String))
    } catch {
    }
    try {
        Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir
    } catch {
    }
}
";
        }
    }
}
