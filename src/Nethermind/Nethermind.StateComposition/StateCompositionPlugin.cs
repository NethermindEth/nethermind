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

public class StateCompositionPlugin(IStateCompositionConfig config) : INethermindPlugin
{
    private INethermindApi? _api;

    public string Name => "StateComposition";
    public string Description => "State composition metrics";
    public string Author => "Nethermind";

    public bool Enabled => config.Enabled;
    public bool MustInitialize => true;
    public IModule Module => new StateCompositionModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _api.Context.Resolve<StateCompositionService>();

        if (!config.PersistSnapshots) return Task.CompletedTask;

        ILogger logger = _api.LogManager.GetClassLogger<StateCompositionPlugin>();

        StateCompositionSnapshotStore store = _api.Context.Resolve<StateCompositionSnapshotStore>();
        StateCompositionStateHolder stateHolder = _api.Context.Resolve<StateCompositionStateHolder>();
        IBlockTree blockTree = _api.Context.Resolve<IBlockTree>();

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
        Metrics.UpdateDepthDistribution(stateHolder.CurrentDepthStats);
        Metrics.StateCompIncrementalBlock = snap.BlockNumber;
        Metrics.StateCompDiffsSinceBaseline = snap.DiffsSinceBaseline;

        if (logger.IsInfo)
            logger.Info($"StateComposition: restored from snapshot at block {snap.BlockNumber}, " +
                        $"diffs={snap.DiffsSinceBaseline}, accounts={snap.Stats.AccountsTotal}");

        return Task.CompletedTask;
    }

    public Task InitRpcModules() => Task.CompletedTask;
}
