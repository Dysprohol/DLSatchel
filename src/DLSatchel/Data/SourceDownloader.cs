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
    /// <summary>Why a list could not be downloaded, so the window can say what to do.</summary>
    public enum ListProblem
    {
        None,
        /// <summary>No connection to the server (no internet, blocked, timed out).</summary>
        Offline,
        /// <summary>The link no longer gives a list: deleted, moved, made private or wrong.</summary>
        LinkBroken,
        /// <summary>A list came, but it is empty, unreadable or much smaller than before.</summary>
        ListChanged,
        /// <summary>The list file on this PC could not be replaced (open in Excel, no access).</summary>
        FileLocked
    }

    /// <summary>Result of refreshing one item list from its link.</summary>
    public sealed record SourceUpdateResult(bool Updated, string FileName, string Message, string? Path = null,
                                            ListProblem Problem = ListProblem.None, string? Link = null);

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

        public static string BackupDirectory => System.IO.Path.Combine(ListsDirectory, "previous");

        /// <summary>The usual link for a list the user already has as a file, recognized by its name.</summary>
        public static string? SuggestLink(string fileName)
        {
            string name = fileName.ToLowerInvariant();
            if (name.Contains("rubyelf") || name.Contains("dream light id list")) return RubyelfLink;
            if (name.Contains("companies")) return null;
            if (name.Contains("kosddv") || name.Contains("item dump")) return KosddvLink;
            return null;
        }

        /// <summary>
        /// Short, readable file names for the two known lists (instead of the long sheet titles).
        /// <see cref="SuggestLink"/> still recognizes them by "kosddv" and "rubyelf".
        /// </summary>
        internal static string? KnownFileName(Uri downloadUri)
        {
            if (downloadUri == ToDownloadUri(KosddvLink)) return "KOSDDV item dump.xlsx";
            if (downloadUri == ToDownloadUri(RubyelfLink)) return "Rubyelf ID list.xlsx";
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
        /// Downloads the list behind <paramref name="link"/> and, if it reads well, keeps it in
        /// <see cref="ListsDirectory"/>: under the name of <paramref name="targetPath"/> (replacing it
        /// when the target is already there), or under the name the server gives. The result's Path
        /// is where the list now is; callers point the source entry there.
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
                int status = (int)response.StatusCode;
                if (status is 408 or 429 or >= 500)
                    return new(false, label,
                        $"the server is busy or unavailable right now (it answered {status} {response.ReasonPhrase}). Try again later.",
                        Problem: ListProblem.Offline, Link: link);
                if (!response.IsSuccessStatusCode)
                    return new(false, label,
                        $"the link does not work any more (the server answered {(int)response.StatusCode} {response.ReasonPhrase}). " +
                        "The sheet may have been deleted, moved or made private.", Problem: ListProblem.LinkBroken, Link: link);

                string serverName = SafeFileName(
                    response.Content.Headers.ContentDisposition?.FileNameStar ??
                    response.Content.Headers.ContentDisposition?.FileName ?? "");
                string extension = System.IO.Path.GetExtension(targetPath ?? serverName);
                if (!SourcesConfigStore.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    extension = ".xlsx";
                if (targetPath == null)
                    label = KnownFileName(uri) is string known
                        ? known
                        : serverName.Length > 0
                        ? serverName
                        // No name from the server: tell lists apart by their link, so two of them never share a file.
                        : $"Item list {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(link)))[..8]}{extension}";

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
                            return new(false, label, "the file is larger than 200 MB, so it was not used.",
                                       Problem: ListProblem.ListChanged, Link: link);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                string? formatProblem = CheckFormat(temp, extension);
                if (formatProblem != null)
                    return new(false, label, formatProblem, Problem: ListProblem.LinkBroken, Link: link);

                var check = await InspectAsync(temp, cancellationToken).ConfigureAwait(false);
                if (check.Error != null)
                    return new(false, label, $"the downloaded list could not be read ({check.Error}).",
                               Problem: ListProblem.ListChanged, Link: link);
                if (check.UniqueIds == 0)
                    return new(false, label, "the downloaded list has no item IDs the editor can read. Its layout may have changed.",
                               Problem: ListProblem.ListChanged, Link: link);
                if (currentItems > 0 && check.UniqueIds < currentItems * MinimumShareOfCurrent)
                    return new(false, label,
                        $"the downloaded list has only {check.UniqueIds:N0} items and yours has {currentItems:N0}. " +
                        "The sheet may have been emptied or changed, so your list was kept.",
                        Problem: ListProblem.ListChanged, Link: link);

                // Downloads live in the editor's own folder. A list the player picked elsewhere
                // (Downloads, a shared folder …) is never overwritten; its entry moves to the copy.
                string destination = targetPath != null && SourceCleanup.IsInside(targetPath, ListsDirectory)
                    ? targetPath
                    : System.IO.Path.Combine(ListsDirectory, targetPath != null ? System.IO.Path.GetFileName(targetPath) : label);
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
                return new(true, System.IO.Path.GetFileName(destination), message, destination, Link: link);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(false, label, "the server did not answer in time. Check your internet connection.",
                           Problem: ListProblem.Offline, Link: link);
            }
            catch (HttpRequestException ex)
            {
                return new(false, label,
                    $"the server could not be reached. Check your internet connection. ({ex.Message})",
                    Problem: ListProblem.Offline, Link: link);
            }
            catch (ArgumentException ex)
            {
                return new(false, label, ex.Message, Problem: ListProblem.LinkBroken, Link: link);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(false, label, ex.Message + " If the list is open in Excel, close it and try again.",
                           Problem: ListProblem.FileLocked, Link: link);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(false, label, ex.Message, Problem: ListProblem.ListChanged, Link: link);
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
