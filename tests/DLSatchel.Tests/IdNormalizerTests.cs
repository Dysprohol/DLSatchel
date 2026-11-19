using System;
using DDV_SaveEditor_G.Data;
using Xunit;

namespace DDV.SaveEditor.Tests
{
    public class IdNormalizerTests
    {
        [Theory]
        [InlineData("10000002", 10000002L)]
        [InlineData(" 10000002 ", 10000002L)]
        [InlineData("10000002.0", 10000002L)]
        [InlineData("1.0000002E7", 10000002L)]
        [InlineData("\"10000002\"", 10000002L)]
        [InlineData("#10000002", 10000002L)]
        [InlineData("0x7C92DFE6", 2090000358L)]
        [InlineData("7C92DFE6", 2090000358L)]
        [InlineData("989682", 989682L)]
        [InlineData("1\u00A0000\u00A0002", 1000002L)]
        public void TryNormalize_AcceptsRealWorldShapes(string raw, long expected)
        {
            Assert.True(IdNormalizer.TryNormalize(raw, out long id, out _, out string? error), error);
            Assert.Equal(expected, id);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("12.5")]
        [InlineData("1,0000")]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("99999999999")]
        public void TryNormalize_RejectsGarbage(string raw)
        {
            Assert.False(IdNormalizer.TryNormalize(raw, out _, out _, out string? error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void HexColumn_IsAlwaysParsedAsHex()
        {
            Assert.True(IdNormalizer.TryNormalizeHex("989682", out long id, out _, out _));
            Assert.Equal(10000002L, id);

            // The same text in the decimal column is a plain decimal id.
            Assert.True(IdNormalizer.TryNormalize("989682", out long decimalId, out _, out _));
            Assert.Equal(989682L, decimalId);
        }
    }
}
