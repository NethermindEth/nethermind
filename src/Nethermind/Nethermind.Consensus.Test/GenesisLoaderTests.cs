// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class GenesisLoaderTests
{
    [Test]
    public void Load_ShouldFlushCacheAfterSuccessfulGenesisProcessing()
    {
        // Arrange
        Block genesisBlock = Build.A.Block.Genesis.TestObject;

        IGenesisBuilder genesisBuilder = Substitute.For<IGenesisBuilder>();
        genesisBuilder.Build().Returns(genesisBlock);

        IStateReader stateReader = Substitute.For<IStateReader>();

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ =>
        {
            // Simulate block processing by triggering NewHeadBlock event
            blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(genesisBlock));
        });

        IWorldState worldState = Substitute.For<IWorldState>();
        IDisposable scopeDisposable = Substitute.For<IDisposable>();
        worldState.BeginScope(IWorldState.PreGenesis).Returns(scopeDisposable);

        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();

        GenesisLoader.Config config = new(null, TimeSpan.FromSeconds(10));
        ILogManager logManager = LimboLogs.Instance;

        GenesisLoader loader = new(
            genesisBuilder,
            stateReader,
            blockTree,
            worldState,
            worldStateManager,
            blockchainProcessor,
            config,
            logManager
        );

        // Act
        loader.Load();

        // Assert - verify FlushCache was called
        worldStateManager.Received(1).FlushCache(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Load_ShouldNotFlushCache_WhenGenesisProcessingTimesOut()
    {
        // Arrange
        Block genesisBlock = Build.A.Block.Genesis.TestObject;

        IGenesisBuilder genesisBuilder = Substitute.For<IGenesisBuilder>();
        genesisBuilder.Build().Returns(genesisBlock);

        IStateReader stateReader = Substitute.For<IStateReader>();

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ =>
        {
            // Do nothing - simulate timeout
        });

        IWorldState worldState = Substitute.For<IWorldState>();
        IDisposable scopeDisposable = Substitute.For<IDisposable>();
        worldState.BeginScope(IWorldState.PreGenesis).Returns(scopeDisposable);

        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();

        GenesisLoader.Config config = new(null, TimeSpan.FromMilliseconds(100));
        ILogManager logManager = LimboLogs.Instance;

        GenesisLoader loader = new(
            genesisBuilder,
            stateReader,
            blockTree,
            worldState,
            worldStateManager,
            blockchainProcessor,
            config,
            logManager
        );

        // Act & Assert - expect timeout exception
        Assert.Throws<TimeoutException>(() => loader.Load());

        // Verify FlushCache was NOT called since genesis processing failed
        worldStateManager.DidNotReceive().FlushCache(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Load_ShouldNotFlushCache_WhenGenesisBlockIsInvalid()
    {
        // Arrange
        Block genesisBlock = Build.A.Block.Genesis.TestObject;

        IGenesisBuilder genesisBuilder = Substitute.For<IGenesisBuilder>();
        genesisBuilder.Build().Returns(genesisBlock);

        IStateReader stateReader = Substitute.For<IStateReader>();

        IBlockTree blockTree = Substitute.For<IBlockTree>();

        IWorldState worldState = Substitute.For<IWorldState>();
        IDisposable scopeDisposable = Substitute.For<IDisposable>();
        worldState.BeginScope(IWorldState.PreGenesis).Returns(scopeDisposable);

        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ =>
        {
            // Simulate invalid block by triggering InvalidBlock event
            blockchainProcessor.InvalidBlock += Raise.EventWith(
                blockchainProcessor,
                new IBlockchainProcessor.InvalidBlockEventArgs { InvalidBlock = genesisBlock });
        });

        GenesisLoader.Config config = new(null, TimeSpan.FromSeconds(10));
        ILogManager logManager = LimboLogs.Instance;

        GenesisLoader loader = new(
            genesisBuilder,
            stateReader,
            blockTree,
            worldState,
            worldStateManager,
            blockchainProcessor,
            config,
            logManager
        );

        // Act & Assert - expect InvalidBlockException
        Assert.Throws<InvalidBlockException>(() => loader.Load());

        // Verify FlushCache was NOT called since genesis was invalid
        worldStateManager.DidNotReceive().FlushCache(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Load_ShouldFlushCacheAfterScopeExit()
    {
        // Arrange
        Block genesisBlock = Build.A.Block.Genesis.TestObject;

        IGenesisBuilder genesisBuilder = Substitute.For<IGenesisBuilder>();
        genesisBuilder.Build().Returns(genesisBlock);

        IStateReader stateReader = Substitute.For<IStateReader>();

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        bool scopeExited = false;
        blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ =>
        {
            // Simulate block processing by triggering NewHeadBlock event
            blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(genesisBlock));
        });

        IWorldState worldState = Substitute.For<IWorldState>();
        IDisposable scopeDisposable = Substitute.For<IDisposable>();
        scopeDisposable.When(x => x.Dispose()).Do(_ => scopeExited = true);
        worldState.BeginScope(IWorldState.PreGenesis).Returns(scopeDisposable);

        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager.When(x => x.FlushCache(Arg.Any<CancellationToken>())).Do(_ =>
        {
            // Verify that scope was exited before FlushCache was called
            scopeExited.Should().BeTrue("FlushCache should be called after scope exit");
        });

        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();

        GenesisLoader.Config config = new(null, TimeSpan.FromSeconds(10));
        ILogManager logManager = LimboLogs.Instance;

        GenesisLoader loader = new(
            genesisBuilder,
            stateReader,
            blockTree,
            worldState,
            worldStateManager,
            blockchainProcessor,
            config,
            logManager
        );

        // Act
        loader.Load();

        // Assert - verify FlushCache was called after scope exit
        worldStateManager.Received(1).FlushCache(Arg.Any<CancellationToken>());
    }
}
