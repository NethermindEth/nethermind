// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Test;

public class EraPathUtilsTests
{
    [TestCase("test", 0, "0x0000000000000000000000000000000000000000000000000000000000000000", "test-00000-00000000.erae")]
    [TestCase("goerli", 1, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "goerli-00001-ffffffff.erae")]
    [TestCase("mainnet", 2, "0x1122ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "mainnet-00002-1122ffff.erae")]
    public void Filename_ValidParameters_ReturnsExpected(string network, int epoch, string hash, string expected)
    {
        Assert.That(EraPathUtils.Filename(network, epoch, new Hash256(hash)), Is.EqualTo(expected));
    }

    [Test]
    public void Filename_NetworkIsNull_ThrowsArgumentNullException()
    {
#pragma warning disable CS8625
        Assert.That(() => EraPathUtils.Filename(null, 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentNullException);
#pragma warning restore CS8625
    }

    [Test]
    public void Filename_NetworkIsEmpty_ThrowsArgumentException()
    {
        Assert.That(() => EraPathUtils.Filename("", 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentException);
    }

    [Test]
    public void Filename_EpochIsNegative_ThrowsArgumentOutOfRangeException()
    {
        Assert.That(() => EraPathUtils.Filename("test", -1, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Filename_RootIsNull_ThrowsArgumentNullException()
    {
#pragma warning disable CS8625
        Assert.That(() => EraPathUtils.Filename("test", 0, null), Throws.ArgumentNullException);
#pragma warning restore CS8625
    }

    [Test]
    public void ExtractHashFromChecksumEntry_HashOnly_ExtractsCorrectly()
    {
        string input = "0xaabbccdd00000000000000000000000000000000000000000000000000000000";
        var result = EraPathUtils.ExtractHashFromChecksumEntry(input);
        Assert.That(result.ToByteArray()[0], Is.EqualTo(0xaa));
    }

    [Test]
    public void ExtractHashFromChecksumEntry_HashWithFilename_ExtractsHashPart()
    {
        string input = "0xaabbccdd00000000000000000000000000000000000000000000000000000000 test-00000-aabbccdd.erae";
        var result = EraPathUtils.ExtractHashFromChecksumEntry(input);
        Assert.That(result.ToByteArray()[0], Is.EqualTo(0xaa));
    }
}
