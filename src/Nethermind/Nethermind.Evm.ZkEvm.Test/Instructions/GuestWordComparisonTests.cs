// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test.Instructions;

/// <summary>
/// LT, GT, SLT and SGT over big-endian stack words, which only the guest build reaches: a host keeps the
/// <c>IOpMath2Param</c> forms that byte-swap both operands into <see cref="UInt256"/> first.
/// </summary>
/// <remarks>
/// Every case is checked against the host operation it replaces, so the two cannot drift. The cases are
/// chosen for the two things the guest forms do differently: the run of limb equality tests, which needs
/// operands differing in each limb position in turn including none at all, and the sign test, which needs
/// both sides of the two's-complement boundary. A mainnet block exercises the unsigned pair tens of
/// thousands of times but signed operands only rarely, and negative ones barely at all.
/// </remarks>
public class GuestWordComparisonTests
{
    /// <summary>The two's-complement sign boundary: the smallest signed value, 2^255.</summary>
    private static UInt256 MostNegative => new(0, 0, 0, 0x8000000000000000UL);

    private static IEnumerable<TestCaseData> Pairs()
    {
        (string Name, UInt256 Value)[] values =
        [
            ("zero", UInt256.Zero),
            ("one", UInt256.One),
            ("two", new UInt256(2, 0, 0, 0)),
            ("limb0", new UInt256(0xFFFFFFFFFFFFFFFFUL, 0, 0, 0)),
            ("limb1", new UInt256(0, 1, 0, 0)),
            ("limb2", new UInt256(0, 0, 1, 0)),
            ("limb3", new UInt256(0, 0, 0, 1)),
            ("mostNegative", MostNegative),
            ("justBelowSign", MostNegative - UInt256.One),
            // All ones: the largest unsigned value and -1 signed, so it separates the two orderings.
            ("max", UInt256.MaxValue),
        ];

        foreach ((string leftName, UInt256 left) in values)
        {
            foreach ((string rightName, UInt256 right) in values)
            {
                yield return new TestCaseData(left, right).SetName($"{{m}}({leftName},{rightName})");
            }
        }
    }

    [TestCaseSource(nameof(Pairs))]
    public void Unsigned_comparisons_match_the_host_forms(UInt256 a, UInt256 b) =>
        Assert.Multiple(() =>
        {
            Assert.That(Run<EvmInstructions.OpLtWord>(a, b), Is.EqualTo(Reference<EvmInstructions.OpLt>(a, b)), "LT");
            Assert.That(Run<EvmInstructions.OpGtWord>(a, b), Is.EqualTo(Reference<EvmInstructions.OpGt>(a, b)), "GT");
        });

    [TestCaseSource(nameof(Pairs))]
    public void Signed_comparisons_match_the_host_forms(UInt256 a, UInt256 b) =>
        Assert.Multiple(() =>
        {
            Assert.That(Run<EvmInstructions.OpSLtWord>(a, b), Is.EqualTo(Reference<EvmInstructions.OpSLt>(a, b)), "SLT");
            Assert.That(Run<EvmInstructions.OpSGtWord>(a, b), Is.EqualTo(Reference<EvmInstructions.OpSGt>(a, b)), "SGT");
        });

    /// <summary>Runs a guest word operation and reports whether it answered true.</summary>
    private static bool Run<TOp>(in UInt256 a, in UInt256 b)
        where TOp : struct, EvmInstructions.IOpBitwise
    {
        EvmWord result = TOp.Operation(a.ToBigEndianWord(), b.ToBigEndianWord());
        return result != default;
    }

    /// <summary>Runs the host operation the guest form replaces.</summary>
    private static bool Reference<TOp>(in UInt256 a, in UInt256 b)
        where TOp : struct, EvmInstructions.IOpMath2Param
    {
        TOp.Operation(in a, in b, out UInt256 result);
        return !result.IsZero;
    }
}
