// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class GenesisLoaderTests
{
    [Test]
    public void Load_ShouldPersistStateAfterGenesisProcessing()
    {
        // Arrange
        Block genesisBlock = Build.A.Block.Genesis.TestObject;
        
        IGenesisBuilder genesisBuilder = Substitute.For<IGenesisBuilder>();
        genesisBuilder.Build().Returns(genesisBlock);
        
        IStateReader stateReader = Substitute.For<IStateReader>();
        
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ManualResetEventSlim blockProcessedEvent = new(false);
        blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ => 
        {
            // Simulate block processing by triggering NewHeadBlock event
            blockTree.NewHeadBlock += Raise.EventWith(blockTree, new BlockEventArgs(genesisBlock));
        });
        
        IWorldState worldState = Substitute.For<IWorldState>();
        IDisposable scopeDisposable = Substitute.For<IDisposable>();
        worldState.BeginScope(IWorldState.PreGenesis).Returns(scopeDisposable);
        
        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        
        GenesisLoader.Config config = new(null, TimeSpan.FromSeconds(10));
        ILogManager logManager = LimboLogs.Instance;
        
        GenesisLoader loader = new(
            genesisBuilder,
            stateReader,
            blockTree,
            worldState,
            blockchainProcessor,
            config,
            logManager
        );
        
        // Act
        loader.Load();
        
        // Assert - verify CommitTree was called with block number 0 (genesis)
        worldState.Received(1).CommitTree(0);
    }
    
    [Test]
    public void Load_ShouldNotPersistState_WhenGenesisProcessingFails()
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
        
        IBlockchainProcessor blockchainProcessor = Substitute.For<IBlockchainProcessor>();
        
        GenesisLoader.Config config = new(null, TimeSpan.FromMilliseconds(100));
        ILogManager logManager = LimboLogs.Instance;
        
        GenesisLoader loader = new(
            genesisBuilder,
            stateReader,
            blockTree,
            worldState,
            blockchainProcessor,
            config,
            logManager
        );
        
        // Act & Assert - expect timeout exception
        Assert.Throws<TimeoutException>(() => loader.Load());
        
        // Verify CommitTree was NOT called since genesis processing failed
        worldState.DidNotReceive().CommitTree(Arg.Any<long>());
    }
    
    [Test]
    public void Load_ShouldNotPersistState_WhenGenesisIsInvalid()
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
            blockchainProcessor,
            config,
            logManager
        );
        
        // Act & Assert - expect InvalidBlockException
        Assert.Throws<InvalidBlockException>(() => loader.Load());
        
        // Verify CommitTree was NOT called since genesis was invalid
        worldState.DidNotReceive().CommitTree(Arg.Any<long>());
    }
}
