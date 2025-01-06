// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Humanizer;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Merge.Plugin;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Synchronization.Test.Modules;
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
            .Build();

        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();

        container.Resolve<BlockProcessingModule.MainBlockProcessingContext>().BlockchainProcessor.Start();

        Block block = CreateBlock(container);

        IBlockProcessingQueue blockProcessingQueue = container.Resolve<IBlockProcessingQueue>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        ProcessingResult? res = await ValidateBlockAndProcess(blockProcessingQueue, blockTree, block, ProcessingOptions.None);
        res.Should().Be(ProcessingResult.Success);
    }

    private static Block CreateBlock(IContainer container)
    {
        PrivateKey sender = TestItem.PrivateKeyA;
        IWorldState TestState = container.Resolve<IWorldStateManager>().GlobalWorldState;
        ISpecProvider specProvider = container.Resolve<ISpecProvider>();
        IEthereumEcdsa ecdsa = container.Resolve<IEthereumEcdsa>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        TestState.CreateAccount(sender.Address, 100.Ether());
        TestState.Commit(specProvider.GenesisSpec);
        TestState.CommitTree(0);

        var genesisBlock = Build.A.Block.Genesis.WithStateRoot(TestState.StateRoot).TestObject;
        blockTree.SuggestBlock(genesisBlock);

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

        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        byte[] byteCode1 = Prepare.EvmCode
            .Call(contractAddress, 100000)
            .Op(Instruction.STOP).Done;

        byte[] byteCode2 = Prepare.EvmCode
            .Call(contractAddress, 100000)
            .Op(Instruction.STOP).Done;

        long gasLimit = 1000000;

        Transaction initTx = Build.A.Transaction.WithCode(initByteCode).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
        Transaction tx1 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
        Transaction tx2 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block
            .WithParent(genesisBlock)
            .WithTransactions(initTx, tx1, tx2).WithGasLimit(2 * gasLimit).TestObject;
        return block;
    }

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

}
