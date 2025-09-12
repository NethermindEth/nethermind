// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchainUtil(
    IBlockProducer blockProducer,
    ManualTimestamper timestamper,
    IBlockTree blockTree,
    ITxPool txPool,
    TestBlockchainUtil.Config config
)
{
    public record Config(long SlotTime = 10);

    private Task _previousAddBlock = Task.CompletedTask;

    public async Task<AcceptTxResult[]> AddBlockDoNotWaitForHead(bool mayMissTx, CancellationToken cancellationToken, params Transaction[] transactions)
    {
        _previousAddBlock.IsCompleted.Should().BeTrue("Multiple block produced at once. Please make sure this does not happen for test consistency.");
        TaskCompletionSource tcs = new();
        _previousAddBlock = tcs.Task;

        AcceptTxResult[] txResults = transactions.Select(t => txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
        List<Hash256> expectedHashes = txResults.Zip(transactions)
            .Where((item, _) => item.First == AcceptTxResult.Accepted)
            .Select((item, _) => item.Second.Hash!)
            .ToList();

        timestamper.Add(TimeSpan.FromSeconds(config.SlotTime));
        Block? block;
        int iteration = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            block = await blockProducer.BuildBlock(parentHeader: blockTree.GetProducedBlockParent(null), cancellationToken: cancellationToken);

            if (block is not null)
            {
                HashSet<Hash256> blockTxs = block.Transactions.Select((tx) => tx.Hash!).ToHashSet();
                // Note: It is possible that the block can contain more tx.
                if (expectedHashes.All((tx) => blockTxs.Contains(tx))) break;
            }

            if (mayMissTx) break;

            await Task.Yield();
            if (iteration > 0)
            {
                await Task.Delay(100);
            }
            else if (iteration > 3)
            {
                Assert.Fail("Did not produce expected block");
            }
            iteration++;
        }
        blockTree.SuggestBlock(block!).Should().Be(AddBlockResult.Added);

        tcs.TrySetResult();
        return txResults;
    }

    public async Task AddBlockAndWaitForHead(bool mayMissTx, CancellationToken cancellationToken, params Transaction[] transactions)
    {
        Task waitforHead = WaitAsync(blockTree.WaitForNewBlock(cancellationToken), "timeout waiting for new head");

        await AddBlockDoNotWaitForHead(mayMissTx, cancellationToken, transactions);

        await waitforHead;
    }

    private static async Task WaitAsync(Task task, string error)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(error);
        }
    }
}
