// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtScopeProvider(
    IDb codeDb,
    IPbtDbManager manager,
    IPbtChildHeaderSource childHeaders,
    IStateHeaderProvider stateHeaderProvider,
    IRefCountingMemoryProvider nodeGroupMemory,
    bool isReadOnly,
    IPbtConfig config,
    ILogManager logManager) : IWorldStateScopeProvider
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);
    private readonly PbtResourcePool.Usage _usage = isReadOnly ? PbtResourcePool.Usage.ReadOnlyProcessingEnv : PbtResourcePool.Usage.MainBlockProcessing;

    public bool HasRoot(BlockHeader? baseBlock) => manager.HasStateForBlock(new StateId(baseBlock));

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => this.HasRootForTarget(stateHeaderProvider, targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        this.TryBeginScopeAtBase(stateHeaderProvider, targetBlock, metrics, out scope);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        StateId stateId = new(baseBlock);
        long start = Stopwatch.GetTimestamp();
        if (manager.TryGatherBundle(stateId, _usage) is not { } bundle)
        {
            scope = null;
            return false;
        }

        scope = new PbtWorldStateScope(stateId, baseBlock, bundle, _codeDb, manager, childHeaders, nodeGroupMemory, isReadOnly, config, logManager);
        Metrics.PbtBeginScopeTime.Observe(Stopwatch.GetTimestamp() - start);
        return true;
    }
}
