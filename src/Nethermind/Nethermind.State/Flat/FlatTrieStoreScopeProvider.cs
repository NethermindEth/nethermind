// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.State.Flat;

public class FlatTrieStoreScopeProvider: IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock)
    {
        throw new System.NotImplementedException();
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        throw new System.NotImplementedException();
    }
}

public interface ICanonicalStateRootFinder
{
    public Hash256? GetCanonicalStateRootAtBlock(long blockNumber);
}
