// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        IStateCompositionConfig config = _api.Config<IStateCompositionConfig>();
        if (!config.PersistSnapshots) return Task.CompletedTask;

        ILogger logger = _api.LogManager.GetClassLogger<StateCompositionPlugin>();

        StateCompositionSnapshotStore store = _api.Context.Resolve<StateCompositionSnapshotStore>();
        IStateCompositionStateHolder stateHolder = _api.Context.Resolve<IStateCompositionStateHolder>();
        IBlockTree blockTree = _api.Context.Resolve<IBlockTree>();

        StateCompositionSnapshot? snapshot = store.ReadLatestSnapshot();
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

        if (logger.IsInfo)
            logger.Info($"StateComposition: restored from snapshot at block {snap.BlockNumber}, " +
                        $"diffs={snap.DiffsSinceBaseline}, accounts={snap.Stats.AccountsTotal}");

        return Task.CompletedTask;
    }
}
