// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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
    private Block _genesisBlock;
    private IGenesisBuilder _genesisBuilder;
    private IStateReader _stateReader;
    private IBlockTree _blockTree;
    private IWorldState _worldState;
    private IDisposable _scopeDisposable;
    private IWorldStateManager _worldStateManager;
    private IBlockchainProcessor _blockchainProcessor;

    [SetUp]
    public void Setup()
    {
        _genesisBlock = Build.A.Block.Genesis.TestObject;

        _genesisBuilder = Substitute.For<IGenesisBuilder>();
        _genesisBuilder.Build().Returns(_genesisBlock);

        _stateReader = Substitute.For<IStateReader>();

        _blockTree = Substitute.For<IBlockTree>();

        _worldState = Substitute.For<IWorldState>();
        _scopeDisposable = Substitute.For<IDisposable>();
        _worldState.BeginScope(IWorldState.PreGenesis).Returns(_scopeDisposable);

        _worldStateManager = Substitute.For<IWorldStateManager>();

        _blockchainProcessor = Substitute.For<IBlockchainProcessor>();
    }

    [TearDown]
    public async Task TearDown()
    {
        _scopeDisposable.Dispose();
        await _blockchainProcessor.DisposeAsync();
    }

    private GenesisLoader CreateLoader(TimeSpan timeout)
    {
        GenesisLoader.Config config = new(null, timeout);
        return new(
            _genesisBuilder,
            _stateReader,
            _blockTree,
            _worldState,
            _worldStateManager,
            _blockchainProcessor,
            config,
            LimboLogs.Instance
        );
    }

    private void SimulateSuccessfulBlockProcessing()
    {
        _blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ =>
        {
            _blockTree.NewHeadBlock += Raise.EventWith(_blockTree, new BlockEventArgs(_genesisBlock));
        });
    }

    [Test]
    public void Load_ShouldFlushCacheAfterSuccessfulGenesisProcessing()
    {
        SimulateSuccessfulBlockProcessing();

        GenesisLoader loader = CreateLoader(TimeSpan.FromSeconds(10));
        loader.Load();

        _worldStateManager.Received(1).FlushCache(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Load_ShouldNotFlushCache_WhenGenesisProcessingTimesOut()
    {
        // BlockTree.SuggestBlock does nothing — simulates timeout

        GenesisLoader loader = CreateLoader(TimeSpan.FromMilliseconds(100));

        Assert.Throws<TimeoutException>(() => loader.Load());
        _worldStateManager.DidNotReceive().FlushCache(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Load_ShouldNotFlushCache_WhenGenesisBlockIsInvalid()
    {
        _blockTree.When(x => x.SuggestBlock(Arg.Any<Block>())).Do(_ =>
        {
            _blockchainProcessor.InvalidBlock += Raise.EventWith(
                _blockchainProcessor,
                new IBlockchainProcessor.InvalidBlockEventArgs { InvalidBlock = _genesisBlock });
        });

        GenesisLoader loader = CreateLoader(TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidBlockException>(() => loader.Load());
        _worldStateManager.DidNotReceive().FlushCache(Arg.Any<CancellationToken>());
    }

    [Test]
    public void Load_ShouldFlushCacheAfterScopeExit()
    {
        SimulateSuccessfulBlockProcessing();

        bool scopeExited = false;
        _scopeDisposable.When(x => x.Dispose()).Do(_ => scopeExited = true);
        _worldStateManager.When(x => x.FlushCache(Arg.Any<CancellationToken>())).Do(_ =>
        {
            scopeExited.Should().BeTrue("FlushCache should be called after scope exit");
        });

        GenesisLoader loader = CreateLoader(TimeSpan.FromSeconds(10));
        loader.Load();

        _worldStateManager.Received(1).FlushCache(Arg.Any<CancellationToken>());
    }
}
