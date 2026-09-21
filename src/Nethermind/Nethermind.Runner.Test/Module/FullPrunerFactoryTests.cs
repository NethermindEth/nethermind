// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FullPrunerFactoryTests
{
    [Test]
    public void Flat_backend_with_hybrid_pruning_logs_that_full_prune_is_ignored()
    {
        TestLogger logger = new();
        IPruningConfig pruning = Substitute.For<IPruningConfig>();
        pruning.Mode.Returns(PruningMode.Hybrid);
        pruning.FullPruningTrigger.Returns(FullPruningTrigger.StateDbSize);

        FullPruner pruner = Create(new MemDb(), pruning, logger)
            .Create(Substitute.For<IWorldStateManager>(), Substitute.For<IPruningTrieStore>());

        Assert.That(pruner, Is.Null);
        Assert.That(logger.LogList.Any(static l => l.Contains("patricia-only") && l.Contains("ignored")), Is.True);
    }

    [Test]
    public void Memory_mode_with_manual_trigger_is_silent_on_flat()
    {
        TestLogger logger = new();
        IPruningConfig pruning = Substitute.For<IPruningConfig>();
        pruning.Mode.Returns(PruningMode.Memory);
        pruning.FullPruningTrigger.Returns(FullPruningTrigger.Manual);

        FullPruner pruner = Create(new MemDb(), pruning, logger)
            .Create(Substitute.For<IWorldStateManager>(), Substitute.For<IPruningTrieStore>());

        Assert.That(pruner, Is.Null);
        Assert.That(logger.LogList, Is.Empty);
    }

    private static FullPrunerFactory Create(IDb stateDb, IPruningConfig pruningConfig, TestLogger logger)
    {
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.GetDb<IDb>(DbNames.State).Returns(stateDb);

        return new FullPrunerFactory(
            Substitute.For<IInitConfig>(),
            pruningConfig,
            dbProvider,
            Substitute.For<IBlockTree>(),
            new StateBoundaryStore(new MemDb(), new MemDb(), null),
            Substitute.For<INodeStorageFactory>(),
            Substitute.For<INodeStorage>(),
            Substitute.For<IProcessExitSource>(),
            new ChainSpec(),
            Substitute.For<IFileSystem>(),
            Substitute.For<ITimerFactory>(),
            new CompositePruningTrigger(),
            new OneLoggerLogManager(new ILogger(logger)));
    }
}
