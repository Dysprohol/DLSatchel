using System;
using DDV_SaveEditor_G.Updates;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class UpdateCheckerTests
    {
        [Theory]
        [InlineData("v1.0.0", 1, 0, 0)]
        [InlineData("1.2.3", 1, 2, 3)]
        [InlineData("v1.0.10-beta", 1, 0, 10)]
        [InlineData("1.0.0+42e8f917e0d0da9c590781a63acccdaff78c283a", 1, 0, 0)]
        [InlineData("v2", 2, 0, 0)]
        public void Versions_AreReadFromTagsAndBuildStamps(string text, int major, int minor, int build)
        {
            Assert.True(UpdateChecker.TryParseVersion(text, out var version));
            Assert.Equal(new Version(major, minor, build), version);
        }

        [Fact]
        public void CurrentVersion_IsReadFromTheBuild()
        {
            // Touching the class runs its static setup, which once failed when the window opened.
            Assert.True(UpdateChecker.CurrentVersion > new Version(0, 0, 0));
            Assert.Equal("https://github.com/Dysprohol/DLSatchel/releases", UpdateChecker.ReleasesUrl);
        }

        [Theory]
        [InlineData("")]
        [InlineData("dev")]
        [InlineData(null)]
        public void TextWithoutNumbers_IsNotAVersion(string? text)
            => Assert.False(UpdateChecker.TryParseVersion(text, out _));

        [Fact]
        public void NumberedComparison_TreatsTenAsNewerThanNine()
        {
            UpdateChecker.TryParseVersion("v1.0.10", out var ten);
            UpdateChecker.TryParseVersion("v1.0.9", out var nine);
            Assert.True(ten > nine);
        }

        [Fact]
        public void LatestRelease_ExeUrlSizeAndChecksumAreRead()
        {
            const string json = """
            {
              "tag_name": "v1.0.1",
              "html_url": "https://github.com/Dysprohol/DLSatchel/releases/tag/v1.0.1",
              "assets": [
                { "name": "notes.txt", "size": 10, "browser_download_url": "https://example.invalid/notes.txt" },
                { "name": "DLSatchel.exe", "size": 67806767,
                  "digest": "sha256:9d972d990599c040b66af39c27ba4180e3cbf440b2a99ac32b5bdffa25fa57cd",
                  "browser_download_url": "https://github.com/Dysprohol/DLSatchel/releases/download/v1.0.1/DLSatchel.exe" }
              ]
            }
            """;

            var release = UpdateChecker.ParseRelease(json);

            Assert.NotNull(release);
            Assert.Equal(new Version(1, 0, 1), release!.Version);
            Assert.Equal("v1.0.1", release.Tag);
            Assert.EndsWith("/v1.0.1/DLSatchel.exe", release.ExeUrl);
            Assert.Equal(67806767, release.ExeSize);
            Assert.Equal("9d972d990599c040b66af39c27ba4180e3cbf440b2a99ac32b5bdffa25fa57cd", release.Sha256);
        }

        [Fact]
        public void ReleaseWithoutExe_HasNoDownload()
        {
            var release = UpdateChecker.ParseRelease("""{ "tag_name": "v1.1.0", "html_url": "https://x", "assets": [] }""");
            Assert.NotNull(release);
            Assert.Null(release!.ExeUrl);
        }
    }
}
