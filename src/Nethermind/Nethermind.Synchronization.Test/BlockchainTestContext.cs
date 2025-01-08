// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Network.Rlpx;
using Nethermind.State;
using Nethermind.Synchronization.Test.Modules;
using Nethermind.TxPool;

namespace Nethermind.Synchronization.Test;

public class BlockchainTestContext: IAsyncDisposable
{
    private readonly PrivateKey _nodeKey;

    private readonly IWorldStateManager _worldStateManager;
    private readonly ITxPool _txPool;
    private readonly ISpecProvider _specProvider;
    private readonly IEthereumEcdsa _ecdsa;
    private readonly IBlockTree _blockTree;
    private readonly ManualTimestamper _timestamper;
    private readonly IManualBlockProductionTrigger _blockProductionTrigger;
    private readonly BlockProcessingModule.MainBlockProcessingContext _mainBlockProcessingContext;
    private readonly IBlockProducerRunner _blockProducerRunner;
    private readonly IRlpxHost _rlpxHost;

    public BlockchainTestContext(
        [KeyFilter(TestEnvironmentModule.NodeKey)] PrivateKey nodeKey,
        IWorldStateManager worldStateManager,
        ISpecProvider specProvider,
        IEthereumEcdsa ecdsa,
        IBlockTree blockTree,
        ManualTimestamper timestamper,
        IManualBlockProductionTrigger blockProductionTrigger,
        BlockProcessingModule.MainBlockProcessingContext mainBlockProcessingContext,
        ITxPool txPool,
        IBlockProducerRunner blockProducerRunner,
        ProducedBlockSuggester producedBlockSuggester, // Need to be instantiated,
        IRlpxHost rlpxHost
    )
    {
        _txPool = txPool;
        _nodeKey = nodeKey;
        _worldStateManager = worldStateManager;
        _mainBlockProcessingContext = mainBlockProcessingContext;
        _specProvider = specProvider;
        _ecdsa = ecdsa;
        _blockTree = blockTree;
        _timestamper = timestamper;
        _blockProductionTrigger = blockProductionTrigger;
        _blockProducerRunner = blockProducerRunner;
        _rlpxHost = rlpxHost;

        rlpxHost.Init();
        blockProducerRunner.Start();
        mainBlockProcessingContext.BlockchainProcessor.Start();
    }

    public async Task PrepareGenesis(CancellationToken cancellation)
    {
        Task newHeadTask = Wait.ForEventCondition<BlockEventArgs>(
            cancellation,
            (h) => _blockTree.NewHeadBlock += h,
            (h) => _blockTree.NewHeadBlock -= h,
            (e) => true);

        Block genesis = _mainBlockProcessingContext.GenesisLoader.Load();
        _blockTree.SuggestBlock(genesis);
        await newHeadTask;
    }

    public async Task BuildBlockWithCode(byte[][] codes, CancellationToken cancellation)
    {
        long gasLimit = 100000;

        Hash256 stateRoot = _blockTree.Head?.StateRoot!;
        UInt256 currentNonce = _worldStateManager.GlobalStateReader.GetNonce(stateRoot, _nodeKey.Address);
        IReleaseSpec spec = _specProvider.GetSpec((_blockTree.Head?.Number) + 1 ?? 0, null);
        Transaction[] txs = codes.Select((byteCode) => Build.A.Transaction
            .WithCode(byteCode)
            .WithNonce(currentNonce++)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ecdsa, _nodeKey, spec.IsEip155Enabled).TestObject)
            .ToArray();

        await BuildBlockWithTxs(txs, cancellation);
    }

    private async Task BuildBlockWithTxs(Transaction[] transactions, CancellationToken cancellation)
    {
        Task newBlockTask = Wait.ForEventCondition<BlockReplacementEventArgs>(
            cancellation,
            (h) => _blockTree.BlockAddedToMain += h,
            (h) => _blockTree.BlockAddedToMain -= h,
            (e) => true);

        AcceptTxResult[] txResults = transactions.Select(t => _txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
        foreach (AcceptTxResult acceptTxResult in txResults)
        {
            acceptTxResult.Should().Be(AcceptTxResult.Accepted);
        }

        _timestamper.Add(TimeSpan.FromSeconds(1));
        await _blockProductionTrigger.BuildBlock();
        await newBlockTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _mainBlockProcessingContext.BlockchainProcessor.StopAsync();
        await _blockProducerRunner.StopAsync();
        await _rlpxHost.Shutdown();
    }
}
