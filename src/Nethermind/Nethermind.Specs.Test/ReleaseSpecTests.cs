// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

[TestFixture]
public class ReleaseSpecTests
{
    /// <summary>Precompile numbers the 64-bit mask cannot hold, so membership has to reach the set.</summary>
    /// <remarks>0x100 is RIP-7212 and 0x10001/0x10002 are Taiko's, but 0x8000_0000 is the one that pins the
    /// contract: there <see cref="Address.PrecompileIndexOrNegative"/> overflows and reports negative, so
    /// only the address shape can decide, never the sign of the index.</remarks>
    private static readonly long[] NumbersAboveTheMask = [64, 0x100, 0x10001, 0x10002, 0x8000_0000];

    [TestCaseSource(nameof(NumbersAboveTheMask))]
    public void Precompile_above_the_mask_is_found_through_the_set(long number)
    {
        Address registered = Address.FromNumber((UInt256)(ulong)number);
        IReleaseSpec spec = new SpecWithPrecompileAt(registered);

        Assert.That(spec.IsPrecompile(registered), Is.True);
        Assert.That(spec.IsPrecompile(Address.FromNumber((UInt256)(ulong)number + 1)), Is.False);
    }

    private sealed class SpecWithPrecompileAt(Address address) : ReleaseSpec
    {
        public override FrozenSet<AddressAsKey> BuildPrecompilesCache() => new AddressAsKey[] { address }.ToFrozenSet();
    }
}
