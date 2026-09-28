// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.TxPool.Test;

/// <summary>
/// A minimal <see cref="IBlockTree"/> for TxPool tests that avoids NSubstitute's static state issues
/// when running tests in parallel.
/// </summary>
internal class TestBlockTree : BlockTreeTestDouble
{
    public override BlockHeader FindBestSuggestedHeader() => BestSuggestedHeader!;

    /// <summary>
    /// Lets a test decouple the best downloaded block from <see cref="BlockTreeTestDouble.Head"/> to model a syncing node.
    /// </summary>
    public ulong? BestKnownNumberOverride { get; set; }

    public override ulong BestKnownNumber => BestKnownNumberOverride ?? base.BestKnownNumber;

    public void HealCanonicalChain(Hash256 startHash, ulong maxBlockDepth) { }
}
