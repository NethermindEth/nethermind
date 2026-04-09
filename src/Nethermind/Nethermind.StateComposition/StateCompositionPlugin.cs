// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Logging;

namespace Nethermind.StateComposition;

public class StateCompositionPlugin : INethermindPlugin
{
    private INethermindApi? _api;

    public string Name => "StateComposition";
    public string Description => "State composition metrics";
    public string Author => "Nethermind";
    public bool MustInitialize => false;
    public bool Enabled => _api?.Config<IStateCompositionConfig>().Enabled ?? true;
    public IModule Module => new StateCompositionModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (_api is null) return Task.CompletedTask;

        ILogger logger = _api.LogManager.GetClassLogger<StateCompositionPlugin>();
        IStateCompositionConfig config = _api.Config<IStateCompositionConfig>();

        // Force-instantiate the service so its constructor subscribes to
        // IBlockTree.NewHeadBlock. Without this, lazy DI defers construction
        // until the first RPC call and incremental diffs/metrics never fire.
        _api.Context.Resolve<IStateCompositionService>();

        if (!config.PersistSnapshots) return Task.CompletedTask;

        // Same reason — pruner subscribes to NewHeadBlock in its constructor.
        _api.Context.Resolve<StateCompositionSnapshotPruner>();

        StateCompositionSnapshotStore store = _api.Context.Resolve<StateCompositionSnapshotStore>();
        IStateCompositionStateHolder stateHolder = _api.Context.Resolve<IStateCompositionStateHolder>();
        IBlockTree blockTree = _api.Context.Resolve<IBlockTree>();

        StateCompositionSnapshot? snapshot;
        try
        {
            snapshot = store.ReadLatestSnapshot();
        }
        catch (Exception ex)
        {
            // Legacy snapshot from before ContractsWithStorage/EmptyAccounts existed has 11 stat
            // longs instead of 13; decoder will mis-align and throw. Treat as "no snapshot" so a
            // fresh scan rebuilds the baseline with the new schema.
            if (logger.IsWarn)
                logger.Warn($"StateComposition: failed to decode persisted snapshot ({ex.GetType().Name}: {ex.Message}), discarding and triggering fresh scan");
            return Task.CompletedTask;
        }

        if (snapshot is null)
        {
            if (logger.IsInfo)
                logger.Info("StateComposition: no persisted snapshot found, fresh scan required");
            return Task.CompletedTask;
        }

        StateCompositionSnapshot snap = snapshot.Value;

        // Validate snapshot against canonical chain — reject if reorged
        var header = blockTree.FindHeader(snap.BlockNumber);
        if (header?.StateRoot is null || header.StateRoot != snap.StateRoot)
        {
            if (logger.IsWarn)
                logger.Warn($"StateComposition: persisted snapshot at block {snap.BlockNumber} " +
                            $"has stale state root (reorg?), discarding");
            return Task.CompletedTask;
        }

        stateHolder.RestoreFromSnapshot(snap);

        Metrics.UpdateFromCumulativeStats(snap.Stats);
        // Phase C: if the snapshot carried a persisted CumulativeDepthStats, it has
        // already been restored into _currentDepthStats and marked IsSeeded=true.
        // UpdateFromDepthStats is a no-op when not seeded, so this is safe to call
        // unconditionally — it simply leaves the 149 depth gauges at their cold-start
        // zero values until a fresh scan seeds them.
        Metrics.UpdateFromDepthStats(stateHolder.CurrentDepthStats);
        Metrics.StateCompIncrementalBlock = snap.BlockNumber;
        Metrics.StateCompDiffsSinceBaseline = snap.DiffsSinceBaseline;

        if (logger.IsInfo)
            logger.Info($"StateComposition: restored from snapshot at block {snap.BlockNumber}, " +
                        $"diffs={snap.DiffsSinceBaseline}, accounts={snap.Stats.AccountsTotal}");

        return Task.CompletedTask;
    }
}
