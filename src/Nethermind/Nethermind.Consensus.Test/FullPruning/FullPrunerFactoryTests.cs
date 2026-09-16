// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.FullPruning;

public class FullPrunerFactoryTests
{
    private const string RequiredSpaceWarningSubstring = "required to run full pruning";

    // #6838: a VolumeFreeSpace threshold below FullPruner's own required-space estimate
    // (ChainSizeThresholdFactor% of the chain's estimated pruning size) means automatic pruning
    // can never observe enough free space to start, so the factory should warn about it.
    [TestCase(1L, BlockchainIds.Mainnet, true, TestName = "Warns when the threshold is far below the estimated required space")]
    [TestCase(1_000_000_000L, BlockchainIds.Mainnet, false, TestName = "Does not warn when the threshold is far above the estimated required space")]
    [TestCase(1L, 999_999_999UL, false, TestName = "Does not warn when the chain has no pruning-size estimate")]
    [TestCase(-1L, BlockchainIds.Mainnet, true, TestName = "Warns when the threshold is at the boundary of the estimated required space")]
    [TestCase(-1L, BlockchainIds.Mainnet, false, true, FullPruningTrigger.VolumeFreeSpace, 1L, TestName = "Does not warn when the threshold is 1 MB above the estimated required space")]
    [TestCase(1L, BlockchainIds.Mainnet, false, false, TestName = "Does not warn when AvailableSpaceCheckEnabled is false, even though the threshold is far below the estimated required space")]
    [TestCase(1L, BlockchainIds.Mainnet, false, true, FullPruningTrigger.Manual, TestName = "Does not warn when the trigger is Manual, even though the threshold is far below the estimated required space")]
    public void Warns_only_when_threshold_is_below_estimated_required_space(
        long thresholdMb,
        ulong chainId,
        bool expectWarning,
        bool availableSpaceCheckEnabled = true,
        FullPruningTrigger trigger = FullPruningTrigger.VolumeFreeSpace,
        long boundaryOffsetMb = 0)
    {
        // A negative thresholdMb is a marker for "the largest whole-megabyte threshold that does
        // not exceed the estimated required space" - the real required space is date-extrapolated,
        // so it can only be pinned by reading it at run time rather than hard-coding it. boundaryOffsetMb
        // shifts that boundary (e.g. +1 to land just above it) without duplicating its arithmetic.
        long effectiveThresholdMb = thresholdMb < 0 ? ComputeBoundaryThresholdMb(chainId) + boundaryOffsetMb : thresholdMb;

        string baseDbPath = Path.Combine(Path.GetTempPath(), $"full-pruner-factory-test-{Guid.NewGuid()}");
        try
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsInfo.Returns(true);
            logger.IsWarn.Returns(true);

            IDbFactory dbFactory = Substitute.For<IDbFactory>();
            dbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(_ => new MemDb());
            FullPruningDb fullPruningDb = new(new DbSettings("state", "state"), dbFactory);

            IDbProvider dbProvider = Substitute.For<IDbProvider>();
            dbProvider.StateDb.Returns(fullPruningDb);

            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            timerFactory.CreateTimer(Arg.Any<TimeSpan>()).Returns(Substitute.For<ITimer>());

            FullPrunerFactory factory = new(
                new InitConfig { BaseDbPath = baseDbPath },
                new PruningConfig
                {
                    Mode = PruningMode.Full,
                    FullPruningTrigger = trigger,
                    FullPruningThresholdMb = effectiveThresholdMb,
                    AvailableSpaceCheckEnabled = availableSpaceCheckEnabled
                },
                dbProvider,
                Substitute.For<IBlockTree>(),
                new StateBoundaryStore(new MemDb(), new MemDb(), retentionWindowBlocks: null),
                Substitute.For<INodeStorageFactory>(),
                Substitute.For<INodeStorage>(),
                Substitute.For<IProcessExitSource>(),
                new ChainSpec { ChainId = chainId },
                new Testably.Abstractions.RealFileSystem(),
                timerFactory,
                new CompositePruningTrigger(),
                new OneLoggerLogManager(new(logger)));

            using FullPruner pruner = factory.Create(Substitute.For<IWorldStateManager>(), Substitute.For<IPruningTrieStore>());

            Assert.That(pruner, Is.Not.Null, "factory should still construct the pruner");
            if (expectWarning)
            {
                logger.Received(1).Warn(Arg.Is<string>(text => text.Contains(RequiredSpaceWarningSubstring)));
            }
            else
            {
                logger.DidNotReceive().Warn(Arg.Any<string>());
            }
        }
        finally
        {
            if (Directory.Exists(baseDbPath))
            {
                Directory.Delete(baseDbPath, recursive: true);
            }
        }
    }

    // Mirrors FullPrunerFactory's own formula (FullPruner.ChainSizeThresholdFactor% of the chain's
    // estimated pruning size) so the boundary case tracks a live, date-extrapolated estimate instead
    // of a value that could silently drift out of sync with production.
    private static long ComputeBoundaryThresholdMb(ulong chainId)
    {
        long pruningSizeEstimate = ChainSizes.CreateChainSizeInfo(chainId).PruningSize!.Value;
        long requiredSpace = pruningSizeEstimate * FullPruner.ChainSizeThresholdFactor / 100;
        return requiredSpace / 1_000_000;
    }
}
