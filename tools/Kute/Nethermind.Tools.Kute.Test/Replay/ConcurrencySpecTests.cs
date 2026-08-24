// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class ConcurrencySpecTests
{
    [TestCase("1", new[] { 1 }, TestName = "Single level")]
    [TestCase("16", new[] { 16 }, TestName = "Single level above one")]
    [TestCase("1-64", new[] { 1, 2, 4, 8, 16, 32, 64 }, TestName = "Range doubles to a power of two")]
    [TestCase("1-50", new[] { 1, 2, 4, 8, 16, 32, 50 }, TestName = "Range always includes its bound")]
    [TestCase("4-4", new[] { 4 }, TestName = "Degenerate range")]
    [TestCase("8-9", new[] { 8, 9 }, TestName = "Adjacent range")]
    [TestCase("1,4,12", new[] { 1, 4, 12 }, TestName = "Explicit list")]
    [TestCase("12,4,1", new[] { 1, 4, 12 }, TestName = "List is sorted ascending")]
    [TestCase("4,4,8", new[] { 4, 8 }, TestName = "Duplicates collapse")]
    [TestCase(" 2 , 8 ", new[] { 2, 8 }, TestName = "Whitespace is ignored")]
    [TestCase("1,", new[] { 1 }, TestName = "Trailing comma is tolerated")]
    public void Expands_specification(string spec, int[] expected) =>
        Assert.That(ConcurrencySpec.Parse(spec), Is.EqualTo(expected));

    [TestCase("0", TestName = "Zero is not a level")]
    [TestCase("-4", TestName = "Negative is not a level")]
    [TestCase("64-1", TestName = "Backwards range")]
    [TestCase("abc", TestName = "Not a number")]
    [TestCase("1,x", TestName = "Non-numeric list entry")]
    [TestCase("1-", TestName = "Missing upper bound")]
    [TestCase("2.5", TestName = "Fractional level")]
    // A mistyped sweep must fail before the run rather than silently measuring the wrong load.
    public void Rejects_invalid_specification(string spec) =>
        Assert.That(() => ConcurrencySpec.Parse(spec), Throws.InstanceOf<FormatException>());

    [TestCase("", TestName = "Empty")]
    [TestCase("   ", TestName = "Whitespace")]
    public void Rejects_blank_specification(string spec) =>
        Assert.That(() => ConcurrencySpec.Parse(spec), Throws.InstanceOf<ArgumentException>());
}
