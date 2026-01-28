// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

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
    private FlatWorldStateScope? _lastScope;

    public bool HasRoot(BlockHeader? baseBlock)
    {
        return flatDbManager.HasStateForBlock(new StateId(baseBlock));
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new StateId(baseBlock);
        SnapshotBundle snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);

        return _lastScope = new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            isReadOnly: isReadOnly);
    }

    public void WarmUpOutOfScope(Address address, UInt256? slot, bool isWrite)
    {
        _lastScope?.WarmUpOutOfScope(address, slot, isWrite);
    }
}
