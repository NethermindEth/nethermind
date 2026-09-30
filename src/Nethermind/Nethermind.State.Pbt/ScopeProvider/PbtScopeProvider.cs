// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

public class PbtScopeProvider(
    IDb codeDb,
    IPbtDbManager manager,
    IPbtChildHeaderSource childHeaders,
    IStateHeaderProvider stateHeaderProvider,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage,
    bool isReadOnly,
    ITrieWarmer trieWarmer,
    IPbtConfig config,
    ILogManager? logManager = null) : IWorldStateScopeProvider
{
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, isPersistent: !isReadOnly);
    private readonly ITrieWarmer _trieWarmer = isReadOnly ? new NoopTrieWarmer() : trieWarmer;

    public bool HasRoot(BlockHeader? baseBlock) => manager.HasStateForBlock(new StateId(baseBlock));

    public bool HasStateForTargetBlock(BlockHeader targetBlock) =>
        stateHeaderProvider.TryGetBaseBlock(targetBlock, out BlockHeader? parent) && HasRoot(parent);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        if (stateHeaderProvider.TryGetBaseBlock(targetBlock, out BlockHeader? parent)) return TryBeginScope(parent, metrics, out scope);
        scope = null;
        return false;
    }

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        StateId stateId = new(baseBlock);
        long start = Stopwatch.GetTimestamp();
        if (manager.TryGatherBundle(stateId, usage) is not { } bundle)
        {
            scope = null;
            return false;
        }

        scope = new PbtWorldStateScope(stateId, baseBlock, bundle, _codeDb, manager, childHeaders, resourcePool, usage, isReadOnly, _trieWarmer, config, logManager);
        Metrics.PbtBeginScopeTime.Observe(Stopwatch.GetTimestamp() - start);
        return true;
    }
}
