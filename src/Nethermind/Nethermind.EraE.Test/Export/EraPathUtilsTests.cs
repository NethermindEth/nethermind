// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.EraE.Export;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Export;

public class EraPathUtilsTests
{
    private const string ZeroHashHex = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private const string SampleChecksumHash = "aabbccdd00000000000000000000000000000000000000000000000000000000";
    private static readonly Hash256 ZeroHash = new(ZeroHashHex);

    [TestCase("test", 0, "0x0000000000000000000000000000000000000000000000000000000000000000", "test-00000-00000000-noproofs.ere", TestName = "Genesis")]
    [TestCase("goerli", 1, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "goerli-00001-ffffffff-noproofs.ere", TestName = "MaxHash")]
    [TestCase("mainnet", 2, "0x1122ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "mainnet-00002-1122ffff-noproofs.ere", TestName = "Mainnet")]
    public void Filename_WithValidParameters_ReturnsExpected(string network, int epoch, string hash, string expected) =>
        Assert.That(EraPathUtils.Filename(network, epoch, new Hash256(hash)), Is.EqualTo(expected));

    [TestCaseSource(nameof(InvalidFilenameArguments))]
    public void Filename_WithInvalidArguments_Throws(string? network, long epoch, Hash256? lastBlockHash, Type expected) =>
        Assert.That(() => EraPathUtils.Filename(network!, epoch, lastBlockHash!), Throws.TypeOf(expected));

    [TestCase("mainnet-00000-aabbccdd-noproofs.ere", ExpectedResult = true, TestName = "EreWithProfile")]
    [TestCase("mainnet-00000-aabbccdd-noproofs-noreceipts.ere", ExpectedResult = true, TestName = "EreWithCombinedProfiles")]
    [TestCase("mainnet-00000-aabbccdd.ere", ExpectedResult = true, TestName = "EreDefault")]
    [TestCase("mainnet-00001-aabbccdd.erae", ExpectedResult = true, TestName = "LegacyErae")]
    [TestCase("checksums_sha256.txt", ExpectedResult = false, TestName = "NonEraFile")]
    public bool IsEraFile_WithGivenExtension_ReturnsExpected(string filename) => EraPathUtils.IsEraFile(filename);

    [TestCase("mainnet-00000-aabbccdd-noproofs.ere", ExpectedResult = true, TestName = "Ere")]
    [TestCase("mainnet-00000-aabbccdd.erae", ExpectedResult = false, TestName = "LegacyErae")]
    public bool IsCanonicalEraFile_OnlyTrueForEre(string filename) => EraPathUtils.IsCanonicalEraFile(filename);

    [Test]
    public void GetAllEraFiles_WithMixedExtensionsAndProfiles_ReturnsNetworkEraFiles()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string[] names =
        [
            "mainnet-00000-aabbccdd-noproofs.ere",
            "mainnet-00001-aabbccdd.erae",
            "mainnet-00002-aabbccdd-noproofs-noreceipts.ere",
            "checksums_sha256.txt",
            "other-00000-aabbccdd-noproofs.ere",
        ];
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(directory.Path, name), "");
        }

        string[] found = EraPathUtils.GetAllEraFiles(directory.Path, "mainnet")
            .Select(static file => Path.GetFileName(file)!)
            .Order()
            .ToArray();

        Assert.That(found, Is.EqualTo(new[]
        {
            "mainnet-00000-aabbccdd-noproofs.ere",
            "mainnet-00001-aabbccdd.erae",
            "mainnet-00002-aabbccdd-noproofs-noreceipts.ere",
        }));
    }

    [TestCase("mainnet-00007-aabbccdd-noproofs.ere", ExpectedResult = 7L, TestName = "EreWithProfile")]
    [TestCase("mainnet-00003-aabbccdd.erae", ExpectedResult = 3L, TestName = "LegacyErae")]
    public long ParseChecksumEntry_WithProfileOrLegacyName_ReturnsEpoch(string filename) =>
        EraPathUtils.ParseChecksumEntry($"{SampleChecksumHash}  {filename}").Epoch;

    [TestCase(SampleChecksumHash, TestName = "HashOnly")]
    [TestCase(SampleChecksumHash + "  test-00000-aabbccdd-noproofs.ere", TestName = "HashAndFilename")]
    public void ExtractHashFromChecksumEntry_ReturnsCorrectHash(string input) =>
        Assert.That(EraPathUtils.ExtractHashFromChecksumEntry(input).ToByteArray()[0], Is.EqualTo(0xaa));

    private static IEnumerable<TestCaseData> InvalidFilenameArguments()
    {
        yield return new TestCaseData(null, 0L, ZeroHash, typeof(ArgumentNullException)) { TestName = "NullNetwork" };
        yield return new TestCaseData("", 0L, ZeroHash, typeof(ArgumentException)) { TestName = "EmptyNetwork" };
        yield return new TestCaseData("test", -1L, ZeroHash, typeof(ArgumentOutOfRangeException)) { TestName = "NegativeEpoch" };
        yield return new TestCaseData("test", 0L, null, typeof(ArgumentNullException)) { TestName = "NullHash" };
    }
}
