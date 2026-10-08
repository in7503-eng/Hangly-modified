//
//  Updater.cs
//  Hangly
//
//  Direct GitHub Releases auto-updater for Hangly.
//

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangly.Core.Analytics;
using Hangly.Core.Lifecycle;

namespace Hangly.App.Services;

/// <summary>What a check found.</summary>
/// <param name="Version">The version available, or null when there is none.</param>
/// <param name="Message">What to tell the person.</param>
public readonly record struct UpdateCheck(string? Version, string Message, string? Notes = null)
{
    public bool HasUpdate => Version is not null;

    /// <summary>Whether there is anything to read about this release.</summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
}

/// <summary>Finds updates from GitHub Releases, downloads, and applies them seamlessly.</summary>
public sealed class Updater
{
    // Your GitHub repository: in7503-eng / Hangly-modified
    private const string GitHubOwner = "in7503-eng";
    private const string GitHubRepo = "Hangly-modified";

    public static string Channel =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
            ? "win-arm64"
            : "win-x64";

    private readonly string feedUrl;
    private string? _latestVersion;
    private string? _latestNotes;
    private string? _pendingDownloadUrl;

    public string? ReadyVersion => _latestVersion;
    public string? AvailableVersion => _latestVersion;
    public string? AvailableNotes => _latestNotes;

    public event Action<int>? DownloadProgress;
    public event Action? StateChanged;

    public Updater(string feedUrl)
    {
        this.feedUrl = feedUrl;
    }

    /// <summary>Always returns true so updates work on any PC without installers.</summary>
    public static bool IsInstalled => true;

    /// <summary>Release notes as plain text, ready for the UI box.</summary>
    public static string PlainNotes(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        try
        {
            return Hangly.Core.Text.ReleaseNotes.Plain(markdown);
        }
        catch
        {
            return markdown;
        }
    }

    /// <summary>Asks GitHub Releases what exists. Never throws.</summary>
    public async Task<UpdateCheck> CheckAsync(UpdateTrigger trigger = UpdateTrigger.Quiet)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hangly-Updater", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            string apiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            using var response = await client.GetAsync(apiUrl).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheck(null, "No releases published on GitHub yet. Create a release on GitHub to enable updates.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheck(null, $"GitHub check returned {(int)response.StatusCode}.");
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(tagName))
            {
                return new UpdateCheck(null, "No release tag found on GitHub.");
            }

            // Find any attached .zip asset in the release
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }

            // Compare versions
            string currentVersionStr = GetCurrentVersion();
            Version remoteVer = ParseVersion(tagName);
            Version localVer = ParseVersion(currentVersionStr);

            if (remoteVer > localVer)
            {
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    return new UpdateCheck(null, $"Update {tagName} is available, but no .zip asset is attached on GitHub.");
                }

                _latestVersion = tagName;
                _latestNotes = body;
                _pendingDownloadUrl = downloadUrl;
                StateChanged?.Invoke();

                return new UpdateCheck(tagName, $"Hangly {tagName} is available!", body);
            }

            return new UpdateCheck(null, $"Hangly is up to date (version {currentVersionStr}).");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Update check failed: {ex.Message}");
            return new UpdateCheck(null, "Couldn't check for updates just now.");
        }
    }

    /// <summary>Downloads and applies the update, then restarts the application.</summary>
    public async Task<string> DownloadAndApplyAsync(Action<string>? status = null)
    {
        if (string.IsNullOrEmpty(_pendingDownloadUrl))
        {
            return "There's nothing to install.";
        }

        try
        {
            status?.Invoke("Downloading update… 0%");
            string tempDir = Path.Combine(Path.GetTempPath(), "HanglyUpdate_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, "update.zip");

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hangly-Updater", "1.0"));
                using var response = await client.GetAsync(_pendingDownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int read;
                int lastPercent = -1;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    totalRead += read;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int percent = (int)((totalRead * 100) / totalBytes.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            status?.Invoke($"Downloading {percent}%…");
                            DownloadProgress?.Invoke(percent);
                        }
                    }
                }
            }

            status?.Invoke("Extracting update…");
            string extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // Find directory containing Hangly.exe in the extracted content
            string sourceDir = extractDir;
            string[] exeMatches = Directory.GetFiles(extractDir, "Hangly.exe", SearchOption.AllDirectories);
            if (exeMatches.Length > 0)
            {
                sourceDir = Path.GetDirectoryName(exeMatches[0])!;
            }

            status?.Invoke("Installing and restarting…");
            await Task.Delay(1000).ConfigureAwait(false);

            string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string exePath = Environment.ProcessPath ?? Path.Combine(appDir, "Hangly.exe");
            int currentPid = Environment.ProcessId;

            // Generate self-terminating updater batch script
            string scriptPath = Path.Combine(tempDir, "apply_update.cmd");
            string scriptContent = $@"@echo off
chcp 65001 >nul

:: Wait for running Hangly process ({currentPid}) to exit
:waitloop
tasklist /fi ""PID eq {currentPid}"" 2>nul | find /i ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto waitloop
)

timeout /t 1 /nobreak >nul

:: Copy updated files over current application directory
xcopy /s /e /y /q ""{sourceDir}\*"" ""{appDir}\"" >nul

:: Launch updated application
start """" ""{exePath}""

:: Clean up temp folder
timeout /t 2 /nobreak >nul
rd /s /q ""{tempDir}"" 2>nul
exit
";
            await File.WriteAllTextAsync(scriptPath, scriptContent).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Process.Start(psi);

            // Exit so files are unlocked and replaced
            Environment.Exit(0);
            return "Restarting…";
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Update install failed: {ex.Message}");
            return $"Update failed: {ex.Message}";
        }
    }

    public Task<bool> DownloadAsync() => Task.FromResult(true);
    public void ApplyOnExit() { }
    public bool ApplyQuietlyAndRestart() => true;
    public Task<bool> UpdateInBackgroundAsync() => Task.FromResult(true);

    public async Task<bool> UpdateNowAsync(Action<int> downloading, Action installing, CancellationToken cancel = default)
    {
        string res = await DownloadAndApplyAsync(s => { });
        return res == "Restarting…";
    }

    private static string GetCurrentVersion()
    {
        try
        {
            return AppInfo.Version;
        }
        catch
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
        }
    }

    private static Version ParseVersion(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new Version(0, 0);

        string clean = input.Trim().TrimStart('v', 'V');
        int dashIdx = clean.IndexOf('-');
        if (dashIdx >= 0) clean = clean[..dashIdx];

        if (Version.TryParse(clean, out Version? ver))
        {
            return ver;
        }

        string[] parts = clean.Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out int major)) return new Version(major, 0);
        if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int n)) return new Version(m, n);
        return new Version(0, 0);
    }
}