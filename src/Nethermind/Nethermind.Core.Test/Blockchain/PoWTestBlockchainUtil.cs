// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Events;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Blockchain;

public class PoWTestBlockchainUtil(
    IBlockProducerRunner blockProducerRunner,
    IManualBlockProductionTrigger blockProductionTrigger,
    ManualTimestamper timestamper,
    IBlockTree blockTree,
    ITxPool txPool,
    long slotTime)
{
    private Task _previousAddBlock = Task.CompletedTask;

    public async Task<AcceptTxResult[]> AddBlockDoNotWaitForHead(CancellationToken cancellationToken, params Transaction[] transactions)
    {
        await WaitAsync(_previousAddBlock, "Multiple block produced at once.").ConfigureAwait(false);
        TaskCompletionSource tcs = new();
        _previousAddBlock = tcs.Task;

        Task waitForNewBlock = WaitAsync(WaitForBlockProducerBlockProduced(cancellationToken), "timeout waiting for block producer");

        AcceptTxResult[] txResults = transactions.Select(t => txPool.SubmitTx(t, TxHandlingOptions.None)).ToArray();
        timestamper.Add(TimeSpan.FromSeconds(slotTime));
        await blockProductionTrigger.BuildBlock().ConfigureAwait(false);

        await waitForNewBlock.ConfigureAwait(false);

        tcs.TrySetResult();
        return txResults;
    }

    public async Task AddBlockAndWaitForHead(CancellationToken cancellationToken, params Transaction[] transactions)
    {
        Task waitforHead = WaitAsync(blockTree.WaitForNewBlock(cancellationToken), "timeout waiting for new head");

        await AddBlockDoNotWaitForHead(cancellationToken, transactions);

        await waitforHead;
    }

    private Task WaitForBlockProducerBlockProduced(CancellationToken cancellationToken = default)
    {
        return Wait.ForEventCondition<BlockEventArgs>(cancellationToken,
            e => blockProducerRunner.BlockProduced += e,
            e => blockProducerRunner.BlockProduced -= e,
            b => true);
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
