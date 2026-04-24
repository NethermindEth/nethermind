// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatScopeProvider(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    ITrieWarmer trieWarmer,
    ResourcePool.Usage usage,
    ILogManager logManager,
    bool isReadOnly)
    : IWorldStateScopeProvider
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb);

    private volatile Hash256? _lastCommittedStateRoot;
    private volatile TrieNode? _lastCommittedStateRootNode;

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);

        Hash256 requestedRoot = currentState.StateRoot.ToCommitment();
        TrieNode? cachedRoot = requestedRoot == _lastCommittedStateRoot ? _lastCommittedStateRootNode : null;

        return new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            isReadOnly: isReadOnly,
            cachedStateRoot: cachedRoot,
            scopeProvider: this);
    }

    internal void RecordCommittedStateRoot(Hash256? rootHash, TrieNode? rootNode)
    {
        _lastCommittedStateRoot = rootHash;
        _lastCommittedStateRootNode = rootNode;
    }
}
