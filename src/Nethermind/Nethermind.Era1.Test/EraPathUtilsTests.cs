// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Era1.Test;

public class EraPathUtilsTests
{
    [TestCase("test", 0UL, "0x0000000000000000000000000000000000000000000000000000000000000000", "test-00000-00000000.era1")]
    [TestCase("goerli", 1UL, "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "goerli-00001-ffffffff.era1")]
    [TestCase("sepolia", 2UL, "0x1122ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "sepolia-00002-1122ffff.era1")]
    public void Filename_ValidParameters_ReturnsExpected(string network, ulong epoch, string hash, string expected) => Assert.That(EraPathUtils.Filename(network, epoch, new Hash256(hash)), Is.EqualTo(expected));

    [TestCase(new[] { "abc-00002-1d1be493.era1", "abc-00002-0000000a.era1" }, null, false, null)]
    [TestCase(new[] { "abc-00002-00000000.era1", "abc-00002-1d1be493.era1" }, null, true, "abc-00002-1d1be493.era1")]
    [TestCase(new[] { "abc-00002-0000000a.era1", "abc-00002-1d1be493.era1" }, "abc-00002-1d1be493.era1", true, "abc-00002-1d1be493.era1")]
    public void ResolveEpochFile_WhenNoFileNeedsHashing_ReadsNone(string[] names, string? listedName, bool hasChecksum, string? expected)
    {
        // The files do not exist, so reading any of them would throw.
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Dictionary<long, ValueHash256> checksums = hasChecksum ? new() { [2] = Keccak.Zero.ValueHash256 } : [];

        string? file = EraPathUtils.ResolveEpochFile([.. names.Select(n => Path.Combine(directory, n))], listedName, checksums, 2);

        Assert.That(file, Is.EqualTo(expected is null ? null : Path.Combine(directory, expected)));
    }

    [Test]
    public void Filename_NetworkIsNull_ReturnsException() =>
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => EraPathUtils.Filename(null, 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentNullException);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.


    [Test]
    public void Filename_NetworkIsEmpty_ReturnsException() => Assert.That(() => EraPathUtils.Filename("", 0, new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")), Throws.ArgumentException);

    [Test]
    public void Filename_RootIsNull_ReturnsException() =>
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => EraPathUtils.Filename("test", 0, null), Throws.ArgumentNullException);
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
}
