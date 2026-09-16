// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init;

public class FullPrunerFactory(
    IInitConfig initConfig,
    IPruningConfig pruningConfig,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    StateBoundaryStore stateBoundary,
    INodeStorageFactory nodeStorageFactory,
    INodeStorage mainNodeStorage,
    IProcessExitSource processExit,
    ChainSpec chainSpec,
    IFileSystem fileSystem,
    ITimerFactory timerFactory,
    CompositePruningTrigger compositePruningTrigger,
    ILogManager logManager
) : IFullPrunerFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<FullPrunerFactory>();

    public FullPruner? Create(IWorldStateManager worldStateManager, IPruningTrieStore trieStore)
    {
        IDb stateDb = dbProvider.StateDb;

        if (!pruningConfig.Mode.IsFull() || stateDb is not IFullPruningDb fullPruningDb) return null;

        IChainEstimations chainEstimations = ChainSizes.CreateChainSizeInfo(chainSpec.ChainId);
        string pruningDbPath = fullPruningDb.GetPath(initConfig.BaseDbPath);
        IPruningTrigger? automaticTrigger = CreateAutomaticTrigger(pruningDbPath, chainEstimations);
        if (automaticTrigger is not null)
        {
            compositePruningTrigger.Add(automaticTrigger);
        }

        // Resolve the drive from pruningDbPath rather than the BaseDbPath-keyed registration:
        // if the pruning subdirectory is symlinked or bind-mounted to a different volume, we
        // want the free-space trigger to read that volume, not BaseDbPath's drive.
        IDriveInfo? drive = fileSystem.GetDriveInfos(pruningDbPath).FirstOrDefault();
        return new FullPruner(
            fullPruningDb,
            nodeStorageFactory,
            mainNodeStorage,
            compositePruningTrigger,
            pruningConfig,
            blockTree,
            stateBoundary,
            stateBoundary,
            worldStateManager.GlobalStateReader,
            processExit,
            chainEstimations,
            drive,
            trieStore,
            logManager);
    }

    private IPruningTrigger? CreateAutomaticTrigger(string dbPath, IChainEstimations chainEstimations)
    {
        long threshold = pruningConfig.FullPruningThresholdMb.MB;

        switch (pruningConfig.FullPruningTrigger)
        {
            case FullPruningTrigger.StateDbSize:
                if (_logger.IsInfo) _logger.Info($"Full pruning will activate when the database size reaches {threshold.SizeToString(true)} (={threshold.SizeToString()}).");
                return new PathSizePruningTrigger(dbPath, threshold, timerFactory, fileSystem);
            case FullPruningTrigger.VolumeFreeSpace:
                if (_logger.IsInfo) _logger.Info($"Full pruning will activate when disk free space drops below {threshold.SizeToString(true)} (={threshold.SizeToString()}).");
                WarnIfThresholdBelowRequiredSpace(threshold, chainEstimations);
                return new DiskFreeSpacePruningTrigger(dbPath, threshold, timerFactory, fileSystem);
            default:
                return null;
        }
    }

    /// <summary>
    /// Warns when a configured <see cref="FullPruningTrigger.VolumeFreeSpace"/> threshold is set too low
    /// for automatic pruning to ever observe enough free disk space to start.
    /// </summary>
    /// <remarks>
    /// <c>FullPruner.HaveEnoughDiskSpaceToRun</c> refuses to start pruning unless free space is at
    /// least <see cref="FullPruner.ChainSizeThresholdFactor"/>% of the estimated pruning size. A threshold
    /// set below that requirement can never be observed as sufficient by the time it fires, so automatic
    /// pruning would never run (#6838).
    /// </remarks>
    private void WarnIfThresholdBelowRequiredSpace(long threshold, IChainEstimations chainEstimations)
    {
        if (!pruningConfig.AvailableSpaceCheckEnabled) return;

        long? pruningSizeEstimate = chainEstimations.PruningSize;
        if (pruningSizeEstimate is null) return;

        long requiredSpace = pruningSizeEstimate.Value * FullPruner.ChainSizeThresholdFactor / 100;
        if (threshold <= requiredSpace && _logger.IsWarn)
        {
            _logger.Warn($"Full pruning threshold {threshold.SizeToString(true)} (={threshold.SizeToString()}) is below the estimated {requiredSpace.SizeToString(true)} (={requiredSpace.SizeToString()}) required to run full pruning; automatic pruning may never find enough free disk space to start. Consider raising Pruning.{nameof(IPruningConfig.FullPruningThresholdMb)}.");
        }
    }
}
