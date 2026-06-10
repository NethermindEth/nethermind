// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);

    private readonly Lazy<BalReaderPool?> _balReaderPool = new(() =>
    {
        int configured = configuration.WarmReadConcurrency;
        int concurrency = configured < 0 ? Math.Min(4 * Environment.ProcessorCount, 64) : configured;
        return concurrency >= 1 ? new BalReaderPool(concurrency) : null;
    });

    public bool HasRoot(BlockHeader? baseBlock) => flatDbManager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        StateId currentState = new(baseBlock);
        SnapshotBundle snapshotBundle = flatDbManager.GatherSnapshotBundle(currentState, usage: usage);

        return new FlatWorldStateScope(
            currentState,
            snapshotBundle,
            _codeDb,
            flatDbManager,
            configuration,
            trieWarmer,
            logManager,
            balReaderPool: _balReaderPool.Value,
            isReadOnly: isReadOnly);
    }
}
