// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[TestFixture]
public class SizeExtensionsTests
{
    [TestCase(0)]
    [TestCase(1000)]
    [TestCase(9223372036)] // Int64.MaxValue / 1_000_000_000
    public void CheckOverflow_long(long testCase) => Assert.That(testCase.GB >= 0, Is.True);

    [TestCase(0)]
    [TestCase(1000)]
    [TestCase(2147483647)] // Int32.MaxValue
    public void CheckOverflow_int(int testCase) => Assert.That(testCase.GB >= 0, Is.True);

    // SizeToString computes in integer/decimal arithmetic so that this assembly contributes no
    // FPU instructions to the zkEVM guest. These pin the rendering, which is what the previous
    // double implementation produced - in particular that a rounded-away fraction prints no
    // trailing zero (1025 -> "1KiB", not "1.0KiB").
    [TestCase(0L, "0B")]
    [TestCase(1L, "1B")]
    [TestCase(1023L, "1023B")]
    [TestCase(1024L, "1KiB")]
    [TestCase(1025L, "1KiB")]
    [TestCase(1536L, "1.5KiB")]
    [TestCase(2048L, "2KiB")]
    [TestCase(1048576L, "1MiB")]
    [TestCase(1048577L, "1MiB")]
    [TestCase(1073741824L, "1GiB")]
    [TestCase(1610612736L, "1.5GiB")]
    [TestCase(-1025L, "-1KiB")]
    [TestCase(-1536L, "-1.5KiB")]
    public void SizeToString_binary(long testCase, string expected) =>
        Assert.That(testCase.SizeToString(), Is.EqualTo(expected));

    [TestCase(0L, "0B")]
    [TestCase(999L, "999B")]
    [TestCase(1000L, "1KB")]
    [TestCase(1001L, "1KB")]
    [TestCase(1500L, "1.5KB")]
    [TestCase(1000000L, "1MB")]
    public void SizeToString_si(long testCase, string expected) =>
        Assert.That(testCase.SizeToString(useSi: true), Is.EqualTo(expected));

    [TestCase(1536L, 0, "2KiB")]
    [TestCase(1536L, 1, "1.5KiB")]
    [TestCase(1590L, 2, "1.55KiB")]
    [TestCase(1590L, 3, "1.553KiB")]
    public void SizeToString_precision(long testCase, int precision, string expected) =>
        Assert.That(testCase.SizeToString(precision: precision), Is.EqualTo(expected));

    [Test]
    public void SizeToString_addSpace() =>
        Assert.That(1536L.SizeToString(addSpace: true), Is.EqualTo("1.5 KiB"));

    // The largest suffix saturates rather than indexing past the array.
    [Test]
    public void SizeToString_saturates_at_largest_unit() =>
        Assert.That((1024L * 1024 * 1024 * 1024 * 512).SizeToString(), Is.EqualTo("512TiB"));
}
