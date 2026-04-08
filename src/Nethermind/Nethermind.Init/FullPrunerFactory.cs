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

    public FullPruner? Create(IStateReader stateReader, IPruningTrieStore trieStore)
    {
        IDb stateDb = dbProvider.StateDb;

        if (!pruningConfig.Mode.IsFull() || stateDb is not IFullPruningDb fullPruningDb) return null;

        string pruningDbPath = fullPruningDb.GetPath(initConfig.BaseDbPath);
        IPruningTrigger? automaticTrigger = CreateAutomaticTrigger(pruningDbPath);
        if (automaticTrigger is not null)
        {
            compositePruningTrigger.Add(automaticTrigger);
        }

        IDriveInfo? drive = fileSystem.GetDriveInfos(pruningDbPath).FirstOrDefault();
        return new FullPruner(
            fullPruningDb,
            nodeStorageFactory,
            mainNodeStorage,
            compositePruningTrigger,
            pruningConfig,
            blockTree,
            stateReader,
            processExit,
            ChainSizes.CreateChainSizeInfo(chainSpec.ChainId),
            drive,
            trieStore,
            logManager);
    }

    private IPruningTrigger? CreateAutomaticTrigger(string dbPath)
    {
        long threshold = pruningConfig.FullPruningThresholdMb.MB;

        switch (pruningConfig.FullPruningTrigger)
        {
            case FullPruningTrigger.StateDbSize:
                if (_logger.IsInfo) _logger.Info($"Full pruning will activate when the database size reaches {threshold.SizeToString(true)} (={threshold.SizeToString()}).");
                return new PathSizePruningTrigger(dbPath, threshold, timerFactory, fileSystem);
            case FullPruningTrigger.VolumeFreeSpace:
                if (_logger.IsInfo) _logger.Info($"Full pruning will activate when disk free space drops below {threshold.SizeToString(true)} (={threshold.SizeToString()}).");
                return new DiskFreeSpacePruningTrigger(dbPath, threshold, timerFactory, fileSystem);
            default:
                return null;
        }
    }
}
