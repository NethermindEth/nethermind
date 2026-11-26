// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public partial class StatelessBlockProcessingEnv
{
    private static partial IWorldState CreateWorldState(IWorldStateScopeProvider scopeProvider, ILogManager logManager) =>
        new StatelessExecutingWorldState(new WorldState(scopeProvider, logManager));

    private static partial ICodeCache CreateCodeCache() => new StaticCodeCache(CodeCacheCapacity);

    private static partial IWorldStateScopeProvider CreateScopeProvider(Witness witness, ILogManager logManager) =>
        new TrieStoreScopeProvider(
            // Must not share nodes between lookups: the guest's TrieNode.Unseal mutates written nodes in place.
            WitnessNodeStorage.CreateTrieStore(witness.State), witness.CreateCodeDb(), UnavailableStateHeaderProvider.Instance, logManager);
}
