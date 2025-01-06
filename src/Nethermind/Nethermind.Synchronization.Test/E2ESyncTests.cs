// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Humanizer;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.Synchronization.Test.Modules;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class E2ESyncTests
{

    [Test]
    public async Task E2ESyncTest()
    {
        IConfigProvider configProvider = new ConfigProvider();

        await using IContainer container = new ContainerBuilder()
            .AddModule(new PsudoNethermindModule(configProvider))

            .AddSingleton<BlockchainTestCoordinatorThingyIDontKnowAnymore>()
            .AddSingleton<ISealer>(new NethDevSealEngine(TestItem.AddressA))
            .AddSingleton<ITimestamper, ManualTimestamper>()

            .AddScoped<IChainHeadInfoProvider, IComponentContext>((ctx) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                IBlockTree blockTree = ctx.Resolve<IBlockTree>();
                IWorldState worldState = ctx.Resolve<IWorldState>();
                ICodeInfoRepository codeInfoRepository = ctx.Resolve<ICodeInfoRepository>();
                return new ChainHeadInfoProvider(specProvider, blockTree, worldState, codeInfoRepository)
                {
                    // It just need to override this.
                    HasSynced = true
                };
            })

            .Build();


        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();

        var thething = container.Resolve<BlockchainTestCoordinatorThingyIDontKnowAnymore>();

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(10.Seconds());

        await thething.CreateBlock(cancellationTokenSource.Token);

        // ProcessingResult? res = await ValidateBlockAndProcess(blockProcessingQueue, blockTree, block, ProcessingOptions.None);
        // res.Should().Be(ProcessingResult.Success);
    }

    public class BlockchainTestCoordinatorThingyIDontKnowAnymore: IAsyncDisposable
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        private readonly IWorldStateManager _worldStateManager;
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly IBlockTree _blockTree;
        private readonly ManualTimestamper _timestamper;
        private readonly IManualBlockProductionTrigger _blockProductionTrigger;
        private readonly BlockProcessingModule.MainBlockProcessingContext _mainBlockProcessingContext;
        private readonly IBlockProducerRunner _blockProducerRunner;

        public BlockchainTestCoordinatorThingyIDontKnowAnymore(IWorldStateManager worldStateManager,
            ISpecProvider specProvider,
            IEthereumEcdsa ecdsa,
            IBlockTree blockTree,
            ManualTimestamper timestamper,
            IManualBlockProductionTrigger blockProductionTrigger,
            BlockProcessingModule.MainBlockProcessingContext mainBlockProcessingContext,
            IBlockProducerRunner blockProducerRunner,
            ProducedBlockSuggester producedBlockSuggester // Need to be instantiated
            )
        {
            _worldStateManager = worldStateManager;
            _txPool = mainBlockProcessingContext.TxPool;
            _specProvider = specProvider;
            _ecdsa = ecdsa;
            _blockTree = blockTree;
            _timestamper = timestamper;
            _blockProductionTrigger = blockProductionTrigger;
            _blockProducerRunner = blockProducerRunner;

            _mainBlockProcessingContext = mainBlockProcessingContext;

            blockProducerRunner.Start();
            mainBlockProcessingContext.BlockchainProcessor.Start();
        }

        private async Task PrepareGenesis(CancellationToken cancellation)
        {
            IWorldState TestState = _worldStateManager.GlobalWorldState;

            TestState.CreateAccount(sender.Address, 100.Ether());
            TestState.Commit(_specProvider.GenesisSpec);
            TestState.CommitTree(0);

            Task newHeadTask = Wait.ForEventCondition<BlockEventArgs>(
                cancellation,
                (h) => _blockTree.NewHeadBlock += h,
                (h) => _blockTree.NewHeadBlock -= h,
                (e) => true);

            Block genesisBlock = Build.A.Block.Genesis.WithStateRoot(TestState.StateRoot).TestObject;
            _blockTree.SuggestBlock(genesisBlock);

            await newHeadTask;
        }

        public async Task CreateBlock(CancellationToken cancellation)
        {
            await PrepareGenesis(cancellation);

            PrivateKey sender = TestItem.PrivateKeyA;
            byte[] initByteCode = Prepare.EvmCode
                .ForInitOf(
                    Prepare.EvmCode
                        .PushData(1)
                        .Op(Instruction.SLOAD)
                        .PushData(1)
                        .Op(Instruction.EQ)
                        .PushData(17)
                        .Op(Instruction.JUMPI)
                        .PushData(1)
                        .PushData(1)
                        .Op(Instruction.SSTORE)
                        .PushData(21)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .PushData(0)
                        .Op(Instruction.SELFDESTRUCT)
                        .Op(Instruction.JUMPDEST)
                        .Done)
                .Done;

            Address contractAddress = ContractAddress.From(sender.Address, 0);

            byte[] byteCode1 = Prepare.EvmCode
                .Call(contractAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode2 = Prepare.EvmCode
                .Call(contractAddress, 100000)
                .Op(Instruction.STOP).Done;

            long gasLimit = 1000000;

            // TODO: head + 1
            IReleaseSpec spec = _specProvider.GetSpec(_blockTree.Head?.Number ?? 0, null);
            Transaction initTx = Build.A.Transaction.WithCode(initByteCode).WithGasLimit(gasLimit).SignedAndResolved(_ecdsa, sender, spec.IsEip155Enabled).TestObject;
            Transaction tx1 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(_ecdsa, sender, spec.IsEip155Enabled).TestObject;
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(_ecdsa, sender, spec.IsEip155Enabled).TestObject;

            await AddBlockInternal([initTx, tx1, tx2], cancellation);
        }

        private async Task<AcceptTxResult[]> AddBlockInternal(Transaction[] transactions, CancellationToken cancellation)
        {
            Task newBlockTask = Wait.ForEventCondition<BlockReplacementEventArgs>(
                cancellation,
                (h) => _blockTree.BlockAddedToMain += h,
                (h) => _blockTree.BlockAddedToMain -= h,
                (e) => true);

            Task txHeadChangeTask = Wait.ForEventCondition<Block>(
                cancellation,
                (h) => _txPool.TxPoolHeadChanged += h,
                (h) => _txPool.TxPoolHeadChanged -= h,
                (e) => true);


            AcceptTxResult[] txResults = transactions.Select(t => _txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
            foreach (AcceptTxResult acceptTxResult in txResults)
            {
                acceptTxResult.Should().Be(AcceptTxResult.Accepted);
            }

            _timestamper.Add(TimeSpan.FromSeconds(1));
            await _blockProductionTrigger.BuildBlock();

            await txHeadChangeTask;
            await newBlockTask;
            return txResults;
        }

        public async ValueTask DisposeAsync()
        {
            await _mainBlockProcessingContext.BlockchainProcessor.StopAsync();
            await _blockProducerRunner.StopAsync();
        }
    }

    /*
    private async Task<ProcessingResult?> ValidateBlockAndProcess(IBlockProcessingQueue blockProcessingQueue, IBlockTree blockTree, Block block, ProcessingOptions processingOptions)
    {
        ProcessingResult? result = null;

        TaskCompletionSource<ProcessingResult?> blockProcessedTaskCompletionSource = new();
        Task<ProcessingResult?> blockProcessed = blockProcessedTaskCompletionSource.Task;

        void GetProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs e)
        {
            if (e.BlockHash == block.Hash)
            {
                blockProcessingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;

                if (e.ProcessingResult == ProcessingResult.Exception)
                {
                    BlockchainException? exception = new("Block processing threw exception.", e.Exception);
                    blockProcessedTaskCompletionSource.SetException(exception);
                    return;
                }

                blockProcessedTaskCompletionSource.TrySetResult(e.ProcessingResult);
            }
        }

        blockProcessingQueue.BlockRemoved += GetProcessingQueueOnBlockRemoved;
        try
        {
            Task timeoutTask = Task.Delay(1.Seconds());

            AddBlockResult addResult = await blockTree
                .SuggestBlockAsync(block, BlockTreeSuggestOptions.ForceDontSetAsMain)
                .AsTask().TimeoutOn(timeoutTask);

            result = addResult switch
            {
                AddBlockResult.InvalidBlock => ProcessingResult.ProcessingError,
                // if the block is marked as AlreadyKnown by the block tree then it means it has already
                // been suggested. there are three possibilities, either the block hasn't been processed yet,
                // the block was processed and returned invalid but this wasn't saved anywhere or the block was
                // processed and marked as valid.
                // if marked as processed by the blocktree then return VALID, otherwise null so that it's process a few lines below
                AddBlockResult.AlreadyKnown => blockTree.WasProcessed(block.Number, block.Hash!) ? ProcessingResult.ProcessingError : null,
                _ => null
            };

            if (!result.HasValue)
            {
                // we don't know the result of processing the block, either because
                // it is the first time we add it to the tree or it's AlreadyKnown in
                // the tree but hasn't yet been processed. if it's the second case
                // probably the block is already in the processing queue as a result
                // of a previous newPayload or the block being discovered during syncing
                // but add it to the processing queue just in case.
                blockProcessingQueue.Enqueue(block, processingOptions);
                result = await blockProcessed.TimeoutOn(timeoutTask);
            }
        }
        finally
        {
            blockProcessingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;
        }

        return result;
    }
    */

}
