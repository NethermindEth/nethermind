// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Era1.Test;

public class EraPathUtilsTests
{
    [TestCase("test", 0, "0x0000000000000000000000000000000000000000000000000000000000000000", "test-00000-00000000.era1")]
    [TestCase("goerli", 1, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "goerli-00001-ffffffff.era1")]
    [TestCase("sepolia", 2, "0x1122ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "sepolia-00002-1122ffff.era1")]
    public void Filename_ValidParameters_ReturnsExpected(string network, int epoch, string hash, string expected)
    {
        Assert.That(EraPathUtils.Filename(network, epoch, new Hash256(hash)), Is.EqualTo(expected));
    }

    [Test]
    public void Filename_NetworkIsNull_ReturnsException()
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => EraPathUtils.Filename(null, 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentNullException);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }

    [Test]
    public void Filename_NetworkIsEmpty_ReturnsException()
    {
        Assert.That(() => EraPathUtils.Filename("", 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentException);
    }

    [Test]
    public void Filename_EpochIsNegative_ReturnsException()
    {
        Assert.That(() => EraPathUtils.Filename("test", -1, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Filename_RootIsNull_ReturnsException()
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => EraPathUtils.Filename("test", 0, null), Throws.ArgumentNullException);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }
}
