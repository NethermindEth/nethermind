// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtScopeProvider(
    IDb codeDb, IPbtDbManager manager, IPbtChildHeaderSource childHeaders, IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage, bool isReadOnly, PbtGroupFormat writeFormat) : IWorldStateScopeProvider
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);

    public bool HasRoot(BlockHeader? baseBlock) => manager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        StateId stateId = new(baseBlock);
        return new PbtWorldStateScope(stateId, baseBlock, manager.GatherBundle(stateId, usage), _codeDb, manager, childHeaders, resourcePool, usage, isReadOnly, writeFormat);
    }
}
