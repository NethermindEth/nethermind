// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Eez.Execution.Stateless;

internal sealed class NativeCheckpointProjection(Address[] accounts, ILogManager logManager)
{
    private ProjectionScope? _scope;

    internal IWorldStateScopeProvider CreateProvider(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb) =>
        _scope = new ProjectionScope(trieStore, codeDb, logManager);

    internal Hash256 Project(IWorldState state, IReleaseSpec spec)
    {
        bool needed = false;
        foreach (Address address in accounts)
            if (!state.GetBalance(address).IsZero) needed = true;
        return needed ? _scope!.Project(accounts, spec) : state.StateRoot;
    }

    private sealed class ProjectionScope(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb, ILogManager logManager)
        : TrieStoreScopeProvider(trieStore, codeDb, UnavailableStateHeaderProvider.Instance, logManager)
    {
        private readonly StateTree _projection = new(trieStore.GetTrieStore(null), logManager);

        internal Hash256 Project(Address[] accounts, IReleaseSpec spec)
        {
            BackingStateTree.Commit();
            _projection.RootHash = BackingStateTree.RootHash;
            foreach (Address address in accounts)
            {
                Account? account = _projection.Get(address);
                if (account is null || account.Balance.IsZero) continue;
                Account cleared = account.WithChangedBalance(UInt256.Zero);
                _projection.Set(address, spec.IsEip158Enabled && cleared.IsEmpty ? null : cleared);
            }
            _projection.UpdateRootHash();
            return _projection.RootHash;
        }
    }
}
