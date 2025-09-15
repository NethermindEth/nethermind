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
using Nethermind.Core.Events;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Core.Test.Blockchain;

public class TestBlockchainUtil(
    IBlockProducer blockProducer,
    InvalidBlockDetector invalidBlockDetector,
    ManualTimestamper timestamper,
    IBlockTree blockTree,
    ITxPool txPool,
    TestBlockchainUtil.Config config
)
{
    public record Config(long SlotTime = 10);

    private Task _previousAddBlock = Task.CompletedTask;

    public async Task<AcceptTxResult[]> AddBlock(AddBlockFlags flags, CancellationToken cancellationToken, params Transaction[] transactions)
    {
        Task waitforHead = flags.HasFlag(AddBlockFlags.DoNotWaitForHead)
            ? Task.CompletedTask
            : WaitAsync(blockTree.WaitForNewBlock(cancellationToken), "timeout waiting for new head");

        Task txNewHead = flags.HasFlag(AddBlockFlags.DoNotWaitForHead)
            ? Task.CompletedTask
            : Wait.ForEventCondition<Block>(cancellationToken,
                (h) => txPool.TxPoolHeadChanged += h,
                (h) => txPool.TxPoolHeadChanged -= h,
                b => true);

        Block? invalidBlock = null;
        void OnInvalidBlock(object? sender, InvalidBlockDetector.InvalidBlockEventArgs e)
        {
            invalidBlock = e.InvalidBlock;
        }

        invalidBlockDetector.OnInvalidBlock += OnInvalidBlock;

        bool mayMissTx = (flags & AddBlockFlags.MayMissTx) != 0;
        bool mayHaveExtraTx = (flags & AddBlockFlags.MayHaveExtraTx) != 0;

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

            if (invalidBlock is not null) Assert.Fail($"Invalid block {invalidBlock} produced");

            if (block is not null)
            {
                HashSet<Hash256> blockTxs = block.Transactions.Select((tx) => tx.Hash!).ToHashSet();

                int matchingHashes = expectedHashes.Count((tx) => blockTxs.Contains(tx));
                bool allExpectedHashAvailable = matchingHashes == expectedHashes.Count;
                if (!allExpectedHashAvailable && mayMissTx) break;

                bool hasExtraTx = allExpectedHashAvailable && blockTxs.Count > expectedHashes.Count;
                if (hasExtraTx && mayHaveExtraTx) break;

                bool hasExactlyTheRightTx = expectedHashes.Count == blockTxs.Count;
                if (hasExactlyTheRightTx) break;
            }

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

        await waitforHead;

        await txNewHead; // Wait for tx new head event so that processed tx was removed from txpool

        invalidBlockDetector.OnInvalidBlock -= OnInvalidBlock;
        return txResults;
    }

    public async Task<AcceptTxResult[]> AddBlockDoNotWaitForHead(bool mayMissTx, CancellationToken cancellationToken, params Transaction[] transactions)
    {
        AddBlockFlags flags = AddBlockFlags.DoNotWaitForHead;
        if (mayMissTx) flags |= AddBlockFlags.MayMissTx;

        return await AddBlock(flags, cancellationToken, transactions);
    }

    public async Task AddBlockAndWaitForHead(bool mayMissTx, CancellationToken cancellationToken, params Transaction[] transactions)
    {
        AddBlockFlags flags = AddBlockFlags.None;
        if (mayMissTx) flags |= AddBlockFlags.MayMissTx;

        await AddBlock(flags, cancellationToken, transactions);
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

    [Flags]
    public enum AddBlockFlags
    {
        None = 0,
        DoNotWaitForHead = 1,
        MayMissTx = 2,
        MayHaveExtraTx = 4,
    }
}
