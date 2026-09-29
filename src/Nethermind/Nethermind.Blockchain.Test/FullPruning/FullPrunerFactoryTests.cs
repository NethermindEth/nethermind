// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using Nethermind.State;

namespace Nethermind.Blockchain.Test.FullPruning;

public class FullPrunerFactoryTests
{
    private const ulong ChainWithoutPruningEstimate = 999_999_999UL;

    private static IEnumerable<TestCaseData> ThresholdCases()
    {
        // The required space is date-extrapolated, so the boundary is read at run time.
        long boundaryMb = FullPruner.GetRequiredFreeSpace(ChainSizes.CreateChainSizeInfo(BlockchainIds.Mainnet))!.Value / 1.MB;

        yield return Case(1, BlockchainIds.Mainnet, FullPruningTrigger.VolumeFreeSpace, true).Returns(true).SetName("Far below required space");
        yield return Case(boundaryMb, BlockchainIds.Mainnet, FullPruningTrigger.VolumeFreeSpace, true).Returns(true).SetName("At required space");
        yield return Case(boundaryMb + 1, BlockchainIds.Mainnet, FullPruningTrigger.VolumeFreeSpace, true).Returns(false).SetName("Just above required space");
        yield return Case(1_000_000_000, BlockchainIds.Mainnet, FullPruningTrigger.VolumeFreeSpace, true).Returns(false).SetName("Far above required space");
        yield return Case(1, ChainWithoutPruningEstimate, FullPruningTrigger.VolumeFreeSpace, true).Returns(false).SetName("No pruning-size estimate");
        yield return Case(1, BlockchainIds.Mainnet, FullPruningTrigger.VolumeFreeSpace, false).Returns(false).SetName("Available space check disabled");
        yield return Case(1, BlockchainIds.Mainnet, FullPruningTrigger.Manual, true).Returns(false).SetName("Manual trigger");

        static TestCaseData Case(long thresholdMb, ulong chainId, FullPruningTrigger trigger, bool availableSpaceCheckEnabled) =>
            new(thresholdMb, chainId, trigger, availableSpaceCheckEnabled);
    }

    [TestCaseSource(nameof(ThresholdCases))]
    public bool Warns_when_free_space_threshold_is_at_or_below_required_space(
        long thresholdMb, ulong chainId, FullPruningTrigger trigger, bool availableSpaceCheckEnabled)
    {
        // GetDriveInfos creates the pruning directory on the real disk.
        using TempPath baseDbPath = TempPath.GetTempDirectory();

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
            new InitConfig { BaseDbPath = baseDbPath.Path },
            new PruningConfig
            {
                Mode = PruningMode.Full,
                FullPruningTrigger = trigger,
                FullPruningThresholdMb = thresholdMb,
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

        using FullPruner? pruner = factory.Create(Substitute.For<IWorldStateManager>(), Substitute.For<IPruningTrieStore>());

        Assert.That(pruner, Is.Not.Null);
        return logger.ReceivedCalls().Any(static call =>
            call.GetMethodInfo().Name == nameof(InterfaceLogger.Warn)
            && call.GetArguments() is [string text]
            && text.Contains("required to run full pruning"));
    }
}
