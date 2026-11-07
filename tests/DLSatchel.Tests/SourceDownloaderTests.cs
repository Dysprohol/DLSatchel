using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DDV.SaveEditor.Tests.TestData;
using DDV_SaveEditor_G.Data;
using DDV_SaveEditor_G.Modules;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class SourceDownloaderTests
    {
        public SourceDownloaderTests()
        {
            SourceDownloader.ListsDirectoryOverride = Path.Combine(XlsxBuilder.NewTempDirectory(), "lists");
        }

        [Theory]
        [InlineData("https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/edit?gid=0#gid=0",
                    "https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/export?format=xlsx")]
        [InlineData("https://docs.google.com/spreadsheets/d/e/2PACX-1vAbc_123/pubhtml",
                    "https://docs.google.com/spreadsheets/d/e/2PACX-1vAbc_123/pub?output=xlsx")]
        [InlineData("https://drive.google.com/file/d/1AbC-xyz/view?usp=sharing",
                    "https://drive.google.com/uc?export=download&id=1AbC-xyz")]
        [InlineData("https://example.com/lists/items.xlsx", "https://example.com/lists/items.xlsx")]
        public void SheetLinks_BecomeFileDownloads(string link, string expected)
        {
            Assert.Equal(expected, SourceDownloader.ToDownloadUri(link).ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("C:\\lists\\items.xlsx")]
        [InlineData("ftp://example.com/items.xlsx")]
        public void NonWebLinks_AreRefused(string link)
        {
            Assert.Throws<ArgumentException>(() => SourceDownloader.ToDownloadUri(link));
        }

        [Theory]
        [InlineData("Disney Dream Light ID List - Mainted by Rubyelf.xlsx", SourceDownloader.RubyelfLink)]
        [InlineData("KOSDDV - DDV ITEM DUMP.xlsx", SourceDownloader.KosddvLink)]
        [InlineData("KOSDDV - DDV ITEM DUMP - companies.xlsx", null)]
        [InlineData("my own list.xlsx", null)]
        public void KnownListFiles_GetTheirUsualLink(string fileName, string? expected)
        {
            Assert.Equal(expected, SourceDownloader.SuggestLink(fileName));
        }

        [Fact]
        public async Task GoodDownload_ReplacesTheFile()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string target = XlsxBuilder.WriteCsv(Path.Combine(dir, "items.csv"), Csv(10));
            using var server = new OneFileServer(Csv(12));

            var result = await SourceDownloader.UpdateAsync(server.Url, target, currentItems: 10);

            Assert.True(result.Updated, result.Message);
            Assert.Contains("12 items", result.Message);
            Assert.Equal(Csv(12), File.ReadAllText(target));
        }

        [Fact]
        public async Task WebPageInsteadOfAFile_KeepsTheCurrentFile()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string target = Path.Combine(dir, "items.xlsx");
            File.WriteAllBytes(target, new byte[] { (byte)'P', (byte)'K', 3, 4, 1, 2, 3 });
            using var server = new OneFileServer("<!DOCTYPE html><html><body>Sign in</body></html>");

            var result = await SourceDownloader.UpdateAsync(server.Url, target, currentItems: 10);

            Assert.False(result.Updated);
            Assert.Contains("web page", result.Message);
            Assert.Equal(7, new FileInfo(target).Length);
        }

        [Fact]
        public async Task MuchSmallerList_KeepsTheCurrentFile()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string target = XlsxBuilder.WriteCsv(Path.Combine(dir, "items.csv"), Csv(10));
            using var server = new OneFileServer(Csv(3));

            var result = await SourceDownloader.UpdateAsync(server.Url, target, currentItems: 10);

            Assert.False(result.Updated);
            Assert.Contains("only 3 items", result.Message);
            Assert.Equal(Csv(10), File.ReadAllText(target));
        }

        [Fact]
        public async Task ServerError_KeepsTheCurrentFile()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string target = XlsxBuilder.WriteCsv(Path.Combine(dir, "items.csv"), Csv(10));
            using var server = new OneFileServer("", status: "404 Not Found");

            var result = await SourceDownloader.UpdateAsync(server.Url, target, currentItems: 10);

            Assert.False(result.Updated);
            Assert.Contains("404", result.Message);
            Assert.Equal(Csv(10), File.ReadAllText(target));
        }

        [Fact]
        public async Task ItemsMissingFromTheLists_AreReportedWithTheirSection()
        {
            string dir = XlsxBuilder.NewTempDirectory();
            string source = XlsxBuilder.WriteCsv(Path.Combine(dir, "items.csv"),
                "ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n40000001,Furniture/Chair,Chair,Furniture\n");
            var (catalog, _) = await CatalogHarness.LoadAsync((source, 10));
            ItemDatabase.SetCatalog(catalog);

            var save = JObject.Parse(@"{ ""Player"": {
                ""ListInventories"": { ""0"": { ""CompatibleItemType"": ""ItemType_Furniture"",
                    ""Inventory"": { ""40000001"": { ""Amount"": 1 }, ""40099999"": { ""Amount"": 2 } } } },
                ""Tools"": [ { ""ToolItemID"": 1108999999, ""CurrentOfType"": false } ] } }");

            var unknown = SaveItemIds.NotInCatalog(save);

            Assert.Equal(new[] { (40099999L, "Storage: Furniture"), (1108999999L, "Tools & accessories") },
                         unknown.OrderBy(u => u.Id).ToArray());
        }

        private static string Csv(int items)
        {
            var sb = new StringBuilder("ID,NAME,LOCALIZED_DISPLAY_NAME,TYPE\n");
            for (int i = 0; i < items; i++)
                sb.Append(40000001 + i).Append(",Furniture/Test").Append(i).Append(",Test ").Append(i).Append(",Furniture\n");
            return sb.ToString();
        }

        /// <summary>A tiny local web server that answers every request with the same body.</summary>
        private sealed class OneFileServer : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();

            public OneFileServer(string body, string status = "200 OK")
            {
                _listener.Start();
                byte[] content = Encoding.UTF8.GetBytes(body);
                _ = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        TcpClient client;
                        try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                        catch { return; }
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            await stream.ReadAsync(buffer);
                            string head = $"HTTP/1.1 {status}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                            await stream.WriteAsync(content);
                        }
                    }
                });
            }

            public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/items.csv";

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
            }
        }
    }
}
