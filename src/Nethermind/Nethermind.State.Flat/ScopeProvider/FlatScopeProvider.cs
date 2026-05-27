// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
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
    // Write paths (block processing) wrap the durable production codeDb directly and benefit
    // from the cross-block persisted-code hint cache. Read-only paths wrap a ReadOnlyDb temp
    // buffer (writes are transient) and must NOT populate the hint cache — see TrieStoreScopeProvider.
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    public bool TryBeginScope(BlockHeader? baseBlock, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle? snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);
        if (snapshotBundle is null)
        {
            scope = null;
            return false;
        }

        scope = new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            isReadOnly: isReadOnly);
        return true;
    }
}
