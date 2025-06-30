// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchainUtil(
    IBlockProducer blockProducer,
    ManualTimestamper timestamper,
    IBlockTree blockTree,
    ITxPool txPool,
    long slotTime
)
{
    private Task _previousAddBlock = Task.CompletedTask;

    public async Task<AcceptTxResult[]> AddBlockDoNotWaitForHead(CancellationToken cancellationToken, params Transaction[] transactions)
    {
        _previousAddBlock.IsCompleted.Should().BeTrue("Multiple block produced at once. Please make sure this does not happen for test consistency.");
        TaskCompletionSource tcs = new();
        _previousAddBlock = tcs.Task;

        AcceptTxResult[] txResults = transactions.Select(t => txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
        timestamper.Add(TimeSpan.FromSeconds(slotTime));
        Block? block = await blockProducer.BuildBlock(parentHeader: blockTree.GetProducedBlockParent(null), cancellationToken: cancellationToken);
        blockTree.SuggestBlock(block!).Should().Be(AddBlockResult.Added);

        tcs.TrySetResult();
        return txResults;
    }

    public async Task AddBlockAndWaitForHead(CancellationToken cancellationToken, params Transaction[] transactions)
    {
        Task waitforHead = WaitAsync(blockTree.WaitForNewBlock(cancellationToken), "timeout waiting for new head");

        await AddBlockDoNotWaitForHead(cancellationToken, transactions);

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
