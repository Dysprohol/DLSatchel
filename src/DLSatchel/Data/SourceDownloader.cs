using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>Result of refreshing one item list from its link.</summary>
    public sealed record SourceUpdateResult(bool Updated, string FileName, string Message, string? Path = null);

    /// <summary>
    /// Downloads item lists from a link the user keeps in the Sources window: a Google
    /// Sheets page (turned into its .xlsx export) or a direct link to a file. A download
    /// only replaces the current file after it has been read successfully and holds about
    /// as many items as the current one, so a broken or emptied sheet never wipes the catalog.
    /// </summary>
    public static class SourceDownloader
    {
        public const string RubyelfLink =
            "https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/edit";

        public const string KosddvLink =
            "https://docs.google.com/spreadsheets/d/1uxiBlhMhTtuo7xMd5gGsj7H7GyowfOlJqfA-R_i5jjM/edit";

        /// <summary>A new file must hold at least this share of the current file's items.</summary>
        internal const double MinimumShareOfCurrent = 0.5;

        private const long MaxDownloadBytes = 200L * 1024 * 1024;

        private static readonly Lazy<HttpClient> LazyHttp = new(() =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DLSatchel/{version}");
            return client;
        });

        /// <summary>%LocalAppData%\DDVSaveEditor\lists: where lists added from a link are kept.</summary>
        public static string ListsDirectory => ListsDirectoryOverride ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDVSaveEditor", "lists");

        /// <summary>Tests keep their downloads out of the user's profile.</summary>
        internal static string? ListsDirectoryOverride { get; set; }

        private static string WorkDirectory => System.IO.Path.Combine(ListsDirectory, "download");

        private static string BackupDirectory => System.IO.Path.Combine(ListsDirectory, "previous");

        /// <summary>The usual link for a list the user already has as a file, recognized by its name.</summary>
        public static string? SuggestLink(string fileName)
        {
            string name = fileName.ToLowerInvariant();
            if (name.Contains("rubyelf") || name.Contains("dream light id list")) return RubyelfLink;
            if (name.Contains("companies")) return null;
            if (name.Contains("kosddv") || name.Contains("item dump")) return KosddvLink;
            return null;
        }

        /// <summary>Turns a pasted sheet link into the address of its downloadable file.</summary>
        public static Uri ToDownloadUri(string link)
        {
            if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("This is not a web link. Paste the address of the sheet, starting with https://");

            if (uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase))
            {
                var published = Regex.Match(uri.AbsolutePath, @"^/spreadsheets/d/e/([A-Za-z0-9_-]+)");
                if (published.Success)
                    return new Uri($"https://docs.google.com/spreadsheets/d/e/{published.Groups[1].Value}/pub?output=xlsx");

                var sheet = Regex.Match(uri.AbsolutePath, @"^/spreadsheets/d/([A-Za-z0-9_-]+)");
                if (sheet.Success)
                    return new Uri($"https://docs.google.com/spreadsheets/d/{sheet.Groups[1].Value}/export?format=xlsx");
            }

            if (uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
            {
                var file = Regex.Match(uri.AbsolutePath, @"^/file/d/([A-Za-z0-9_-]+)");
                if (file.Success)
                    return new Uri($"https://drive.google.com/uc?export=download&id={file.Groups[1].Value}");
            }

            return uri;
        }

        /// <summary>
        /// Downloads the list behind <paramref name="link"/> and, if it reads well, copies it
        /// over <paramref name="targetPath"/>. With no target (a list added from a link) it is
        /// kept in <see cref="ListsDirectory"/> under the name the server gives.
        /// <paramref name="currentItems"/> is how many items the current file has (0 = unknown).
        /// </summary>
        public static async Task<SourceUpdateResult> UpdateAsync(
            string link, string? targetPath, int currentItems, CancellationToken cancellationToken = default)
        {
            string label = targetPath != null ? System.IO.Path.GetFileName(targetPath) : link;
            string? temp = null;
            try
            {
                var uri = ToDownloadUri(link);
                Directory.CreateDirectory(WorkDirectory);

                using var response = await LazyHttp.Value
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new(false, label,
                        $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}. " +
                        "Check the link and that the sheet is shared so anyone with the link can view it.");

                string serverName = SafeFileName(
                    response.Content.Headers.ContentDisposition?.FileNameStar ??
                    response.Content.Headers.ContentDisposition?.FileName ?? "");
                string extension = System.IO.Path.GetExtension(targetPath ?? serverName);
                if (!SourcesConfigStore.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    extension = ".xlsx";
                if (targetPath == null)
                    label = serverName.Length > 0 ? serverName : "Item list" + extension;

                temp = System.IO.Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N") + extension);
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxDownloadBytes)
                            return new(false, label, "the file is larger than 200 MB, so it was not used.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                string? formatProblem = CheckFormat(temp, extension);
                if (formatProblem != null)
                    return new(false, label, formatProblem);

                var check = await InspectAsync(temp, cancellationToken).ConfigureAwait(false);
                if (check.Error != null)
                    return new(false, label, $"the downloaded file could not be read ({check.Error}).");
                if (check.UniqueIds == 0)
                    return new(false, label, "the downloaded file has no item IDs the editor can read.");
                if (currentItems > 0 && check.UniqueIds < currentItems * MinimumShareOfCurrent)
                    return new(false, label,
                        $"the downloaded file has only {check.UniqueIds:N0} items and the current one has {currentItems:N0}. " +
                        "The sheet may have been emptied or changed, so the current file was kept.");

                string destination = targetPath ?? System.IO.Path.Combine(ListsDirectory, label);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    Directory.CreateDirectory(BackupDirectory);
                    File.Copy(destination, System.IO.Path.Combine(BackupDirectory, System.IO.Path.GetFileName(destination)), overwrite: true);
                }
                File.Copy(temp, destination, overwrite: true);

                string message = currentItems > 0
                    ? $"updated: {check.UniqueIds:N0} items (was {currentItems:N0})."
                    : $"downloaded: {check.UniqueIds:N0} items.";
                return new(true, System.IO.Path.GetFileName(destination), message, destination);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(false, label, "the download took too long and was stopped.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string reason = ex is IOException and not FileNotFoundException and not DirectoryNotFoundException
                    ? ex.Message + " If the file is open in Excel, close it and try again."
                    : ex.Message;
                return new(false, label, reason);
            }
            finally
            {
                if (temp != null)
                    try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>A page instead of a file usually means a private sheet or a wrong link.</summary>
        internal static string? CheckFormat(string path, string extension)
        {
            var head = new byte[512];
            int length;
            using (var stream = File.OpenRead(path))
                length = stream.Read(head, 0, head.Length);

            bool isZip = length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
                return isZip ? null : NotAFile;

            string text = System.Text.Encoding.UTF8.GetString(head, 0, length).TrimStart().ToLowerInvariant();
            return text.StartsWith("<!doctype html") || text.StartsWith("<html") ? NotAFile : null;
        }

        private const string NotAFile =
            "the link gave a web page instead of a spreadsheet. The sheet may be private, " +
            "or the link may not point to a sheet.";

        /// <summary>Reads a file on its own, without touching the user's sources or cache.</summary>
        private static async Task<SourceDiagnostics> InspectAsync(string path, CancellationToken cancellationToken)
        {
            string work = System.IO.Path.Combine(WorkDirectory, "check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new CatalogLoadOptions
                {
                    UseCache = false,
                    CacheDirectory = work,
                    SourcesPath = System.IO.Path.Combine(work, "sources.json")
                };
                var config = new SourcesConfig
                {
                    Sources = { new CatalogSourceConfig { Path = path, Enabled = true, Priority = 10 } }
                };
                var catalog = await ItemCatalog.LoadFromConfigAsync(config, options, null, cancellationToken)
                    .ConfigureAwait(false);
                return catalog.Diagnostics.Sources.FirstOrDefault()
                       ?? new SourceDiagnostics { File = path, Error = "nothing was read" };
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static string SafeFileName(string name)
        {
            name = name.Trim().Trim('"');
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 120 ? name[..120] : name;
        }
    }
}
