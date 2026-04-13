// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;

namespace Nethermind.StateComposition;

public class StateCompositionPlugin : INethermindPlugin
{
    private IStateCompositionConfig _config = null!;
    private INethermindApi? _api;

    public string Name => "StateComposition";
    public string Description => "State composition metrics";
    public string Author => "Nethermind";

    public bool Enabled => _config?.Enabled ?? false;
    public IModule Module => new StateCompositionModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _config = nethermindApi.Config<IStateCompositionConfig>();

        // Force-instantiate the service so its constructor subscribes to
        // IBlockTree.NewHeadBlock. Without this, lazy DI defers construction
        // until the first RPC call and incremental diffs/metrics never fire.
        // Runs in Init() (not InitRpcModules) so metrics and diffs work even
        // when JSON-RPC is disabled.
        _api.Context.Resolve<StateCompositionService>();

        if (!_config.PersistSnapshots) return Task.CompletedTask;

        ILogger logger = _api.LogManager.GetClassLogger<StateCompositionPlugin>();

        StateCompositionSnapshotStore store = _api.Context.Resolve<StateCompositionSnapshotStore>();
        StateCompositionStateHolder stateHolder = _api.Context.Resolve<StateCompositionStateHolder>();
        IBlockTree blockTree = _api.Context.Resolve<IBlockTree>();

        // Store returns null for both "no snapshot" and "undecodable snapshot" (it logs the latter).
        StateCompositionSnapshot? snapshot = store.ReadLatestSnapshot();
        if (snapshot is null)
        {
            if (logger.IsInfo)
                logger.Info("StateComposition: no persisted snapshot found, fresh scan required");
            return Task.CompletedTask;
        }

        StateCompositionSnapshot snap = snapshot.Value;

        BlockHeader? header = blockTree.FindHeader(snap.BlockNumber);
        if (header?.StateRoot is null || header.StateRoot != snap.StateRoot)
        {
            if (logger.IsWarn)
                logger.Warn($"StateComposition: persisted snapshot at block {snap.BlockNumber} " +
                            $"has stale state root (reorg?), discarding");
            return Task.CompletedTask;
        }

        stateHolder.RestoreFromSnapshot(snap);

        Metrics.UpdateFromCumulativeStats(snap.Stats);
        Metrics.UpdateFromDepthStats(stateHolder.CurrentDepthStats);
        Metrics.StateCompIncrementalBlock = snap.BlockNumber;
        Metrics.StateCompDiffsSinceBaseline = snap.DiffsSinceBaseline;

        if (logger.IsInfo)
            logger.Info($"StateComposition: restored from snapshot at block {snap.BlockNumber}, " +
                        $"diffs={snap.DiffsSinceBaseline}, accounts={snap.Stats.AccountsTotal}");

        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        // Snapshot restore and service instantiation happen in Init() so they run
        // regardless of whether JSON-RPC is enabled. Nothing to do here.
        return Task.CompletedTask;
    }
}
