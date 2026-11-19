using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Updates
{
    /// <summary>The latest release on GitHub and its single-file executable.</summary>
    internal sealed record ReleaseInfo(Version Version, string Tag, string PageUrl,
                                       string? ExeUrl, long ExeSize, string? Sha256);

    /// <summary>
    /// "Check for updates": asks GitHub's API for the latest release and compares its tag with
    /// this build (v1.0.10 is newer than v1.0.9). Nothing is downloaded without a click. The
    /// download is checked against the size and SHA-256 GitHub reports, then the running .exe is
    /// renamed to .old, the new one takes its place and is started; the .old copy is removed at
    /// the next start.
    /// </summary>
    internal static class UpdateChecker
    {
        public const string ReleasesUrl = "https://github.com/Dysprohol/DLSatchel/releases";
        private const string ApiUrl = "https://api.github.com/repos/Dysprohol/DLSatchel/releases/latest";
        private const string AssetName = "DLSatchel.exe";

        // Created on first use: it needs CurrentVersion for its User-Agent, and static fields are
        // initialised in the order they are written.
        private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
        private static HttpClient Http => LazyHttp.Value;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DLSatchel/{CurrentVersion}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>This build's version, e.g. 1.0.0 (the "+commit" suffix is ignored).</summary>
        public static Version CurrentVersion { get; } =
            TryParseVersion(Assembly.GetExecutingAssembly()
                                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                            out var version)
                ? version
                : new Version(0, 0, 0);

        /// <summary>
        /// True when the editor runs as the published single file, the only form it can replace
        /// in place; a build from Visual Studio is never overwritten.
        /// </summary>
        public static bool CanInstallInPlace =>
            string.IsNullOrEmpty(typeof(UpdateChecker).Assembly.Location) && Environment.ProcessPath != null;

        /// <summary>"v1.2.3", "1.2.3-beta" or "1.2.3+abc" → 1.2.3; false when there are no numbers.</summary>
        public static bool TryParseVersion(string? text, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(text)) return false;

            string core = text.Trim().TrimStart('v', 'V');
            int cut = core.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) core = core[..cut];

            if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        /// <summary>Reads the API's "latest release" JSON; null when its tag has no version.</summary>
        public static ReleaseInfo? ParseRelease(string json)
        {
            var release = JObject.Parse(json);
            string tag = release.Value<string>("tag_name") ?? "";
            if (!TryParseVersion(tag, out var version)) return null;

            var exe = (release["assets"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(asset => string.Equals(asset.Value<string>("name"), AssetName, StringComparison.OrdinalIgnoreCase));
            string? digest = exe?.Value<string>("digest");

            return new ReleaseInfo(
                version,
                tag,
                release.Value<string>("html_url") ?? ReleasesUrl,
                exe?.Value<string>("browser_download_url"),
                exe?.Value<long?>("size") ?? 0,
                digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }

        public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            string json = await Http.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            return ParseRelease(json);
        }

        /// <summary>Downloads the release's .exe to a temporary folder and verifies it.</summary>
        public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress,
                                                       CancellationToken cancellationToken = default)
        {
            if (release.ExeUrl == null)
                throw new InvalidOperationException($"Release {release.Tag} has no {AssetName}.");

            string folder = Path.Combine(Path.GetTempPath(), "DLSatchel-update");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, $"DLSatchel-{release.Tag}.exe");

            using (var response = await Http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                            .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.ExeSize;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(target);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var info = new FileInfo(target);
            if (release.ExeSize > 0 && info.Length != release.ExeSize)
                throw new InvalidDataException($"The download has {info.Length} bytes, GitHub lists {release.ExeSize}.");

            if (release.Sha256 != null)
            {
                await using var check = File.OpenRead(target);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download's SHA-256 does not match the release.");
            }

            return target;
        }

        /// <summary>
        /// Puts the downloaded .exe in place of the running one and starts it. Returns false (and
        /// leaves the current .exe untouched) when its folder cannot be written to.
        /// </summary>
        public static bool TryInstallAndRestart(string downloadedExe)
        {
            if (!CanInstallInPlace) return false;
            string current = Environment.ProcessPath!;
            string old = current + ".old";

            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(current, old);          // a running .exe may be renamed, not overwritten
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            try
            {
                File.Copy(downloadedExe, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(old, current);          // put the working version back
                return false;
            }

            Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
            return true;
        }

        /// <summary>Removes the previous version left by an update.</summary>
        public static void DeleteLeftoverFromUpdate()
        {
            if (Environment.ProcessPath is not string current) return;
            try
            {
                string old = current + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the old process may still be closing; next start tries again
            }
        }

        public static void OpenInBrowser(string url)
            => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
