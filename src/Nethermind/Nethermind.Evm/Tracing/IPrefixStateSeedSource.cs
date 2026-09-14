// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Evm.Tracing;

/// <summary>Supplies the state a block's transactions wrote before a given one, so that a trace of that transaction
/// can start from it instead of replaying the transactions ahead of it.</summary>
public interface IPrefixStateSeedSource
{
    /// <summary>Applies and commits onto <paramref name="state"/>, standing at the block's parent, everything the
    /// transactions before <paramref name="transactionIndex"/> wrote. False leaves the state untouched and means the
    /// caller replays the prefix as it always did.</summary>
    bool TrySeed(Block block, int transactionIndex, IWorldState state, IReleaseSpec spec);
}

public sealed class NullPrefixStateSeedSource : IPrefixStateSeedSource
{
    public static readonly NullPrefixStateSeedSource Instance = new();

    private NullPrefixStateSeedSource()
    {
    }

    public bool TrySeed(Block block, int transactionIndex, IWorldState state, IReleaseSpec spec) => false;
}
