// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Humanizer;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.Test.Modules;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class E2ESyncTests
{

    [Test]
    public async Task E2ESyncTest()
    {
        IConfigProvider configProvider = new ConfigProvider();
        ChainSpecLoader specLoader = new ChainSpecLoader(new EthereumJsonSerializer());
        ChainSpec spec = specLoader.LoadEmbeddedOrFromFile("chainspec/foundation.json", default);
        await using IContainer container = new ContainerBuilder()
            .AddModule(new PsudoNethermindModule(configProvider, spec))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA))
            .Build();

        var thething = container.Resolve<BlockchainTestContext>();

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(10.Seconds());

        await thething.PrepareGenesis(cancellationTokenSource.Token);
        await thething.CreateBlock(cancellationTokenSource.Token);
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
