// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

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

    public void InitTxTypesAndRlpDecoders(INethermindApi api) =>
        // Register the snapshot RLP decoder in the global registry so any code
        // using Rlp.Decode<StateCompositionSnapshot>() can resolve it. The local
        // StateCompositionSnapshotStore path still uses the .Instance singleton
        // directly, but global registration keeps the plugin consistent with the
        // rest of the Nethermind RLP ecosystem.
        Rlp.RegisterDecoders(typeof(StateCompositionSnapshotDecoder).Assembly, true);

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        // StateCompositionService is AutoActivated by StateCompositionModule so it
        // has already constructed itself (and wired NewHeadBlock) by this point.

        ILogger logger = _api.LogManager.GetClassLogger<StateCompositionPlugin>();
        IBlockTree blockTree = _api.Context.Resolve<IBlockTree>();
        StateCompositionService service = _api.Context.Resolve<StateCompositionService>();

        if (!config.PersistSnapshots)
        {
            ScheduleBootstrapScan(service, blockTree, logger);
            return Task.CompletedTask;
        }

        StateCompositionSnapshotStore store = _api.Context.Resolve<StateCompositionSnapshotStore>();
        StateCompositionStateHolder stateHolder = _api.Context.Resolve<StateCompositionStateHolder>();

        store.PurgeOldEntries();

        StateCompositionSnapshot? snapshot = store.ReadLatestSnapshot();
        if (snapshot is null)
        {
            if (logger.IsInfo)
                logger.Info("StateComposition: no persisted snapshot found, scheduling bootstrap scan");
            ScheduleBootstrapScan(service, blockTree, logger);
            return Task.CompletedTask;
        }

        StateCompositionSnapshot snap = snapshot.Value;

        BlockHeader? header = blockTree.FindHeader(snap.BlockNumber);
        if (header?.StateRoot is null || header.StateRoot != snap.StateRoot)
        {
            if (logger.IsWarn)
                logger.Warn($"StateComposition: persisted snapshot at block {snap.BlockNumber} " +
                            $"has stale state root (reorg?), scheduling bootstrap scan");
            ScheduleBootstrapScan(service, blockTree, logger);
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

    /// <summary>
    /// Fire-and-forget the bootstrap scan against the current chain head. This is
    /// the only call site that originates from the plugin (the other legal caller
    /// of <see cref="StateCompositionService.AnalyzeAsync"/> is the
    /// MissingTrieNodeException recovery path inside the service itself). If the
    /// block tree has no head yet (very early init), log a warning — operators
    /// must restart the node once the consensus client has driven the head past
    /// genesis. <see cref="StateCompositionService.AnalyzeAsync"/> already
    /// serialises via its scan semaphore, so the dispatched task is safe.
    /// </summary>
    private static void ScheduleBootstrapScan(
        StateCompositionService service,
        IBlockTree blockTree,
        ILogger logger)
    {
        BlockHeader? head = blockTree.Head?.Header;
        if (head is null)
        {
            if (logger.IsWarn)
                logger.Warn("StateComposition: cannot schedule bootstrap scan — block tree head is null");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Result<StateCompositionStats> result =
                    await service.AnalyzeAsync(head, CancellationToken.None).ConfigureAwait(false);

                if (!result.IsSuccess && logger.IsWarn)
                    logger.Warn($"StateComposition: bootstrap scan skipped: {result.Error}");
            }
            catch (Exception ex)
            {
                // Guard the log call itself, not the catch: a `when (IsError)`
                // filter would let the exception escape into the unobserved-task
                // pipeline whenever Error logging is off.
                if (logger.IsError)
                    logger.Error("StateComposition: bootstrap scan failed", ex);
            }
        });
    }
}
