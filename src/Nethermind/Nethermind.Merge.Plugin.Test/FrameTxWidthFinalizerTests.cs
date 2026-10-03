// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class FrameTxWidthFinalizerTests
{
    private const ulong GasUsed = 21_000;

    [Test]
    public void Finalized_block_earns_width_from_its_receipts_once()
    {
        using FinalizingChain chain = new(Enabled(), canonicalLength: 5);
        Block finalized = chain.Canonical[5];

        chain.FinalizeAt(finalized);

        using (Assert.EnterMultipleScope())
        {
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(finalized), Arg.Any<TxReceipt[]>());
            chain.ReceiptFinder.Received(1).Get(Same(finalized), Arg.Any<bool>(), false);
        }
    }

    [Test]
    public void Failure_mid_gap_resumes_without_crediting_a_block_twice([Values] bool lowerFinalizationBeforeResuming)
    {
        using FinalizingChain chain = new(Enabled(), canonicalLength: 8);
        Block first = chain.Canonical[5];
        Block gap = chain.Canonical[6];
        Block last = chain.Canonical[7];
        Block next = chain.Canonical[8];
        int lastAttempts = 0;
        chain.Ledger.When(l => l.EarnWidthOnFinalization(Same(last), Arg.Any<TxReceipt[]>()))
            .Do(_ => { if (++lastAttempts == 1) throw new InvalidOperationException(); });

        chain.FinalizeAt(first);
        chain.FinalizeAt(last);
        if (lowerFinalizationBeforeResuming) chain.FinalizeAt(first);
        chain.FinalizeAt(next);

        using (Assert.EnterMultipleScope())
        {
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(first), Arg.Any<TxReceipt[]>());
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(gap), Arg.Any<TxReceipt[]>());
            chain.Ledger.Received(2).EarnWidthOnFinalization(Same(last), Arg.Any<TxReceipt[]>());
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(next), Arg.Any<TxReceipt[]>());
        }
    }

    [Test]
    public async Task Overlapping_finalizations_credit_each_block_once()
    {
        using FinalizingChain chain = new(Enabled(), canonicalLength: 7);
        Block paused = chain.Canonical[6];
        Block last = chain.Canonical[7];
        using SemaphoreSlim pausedCredits = new(0);
        using ManualResetEventSlim resume = new();
        chain.Ledger.When(l => l.EarnWidthOnFinalization(Same(paused), Arg.Any<TxReceipt[]>()))
            .Do(_ =>
            {
                pausedCredits.Release();
                resume.Wait(TimeSpan.FromSeconds(10));
            });
        chain.FinalizeAt(chain.Canonical[5]);

        Task slower = Task.Run(() => chain.FinalizeAt(paused));
        bool firstCreditPaused = await pausedCredits.WaitAsync(TimeSpan.FromSeconds(10));
        Task overlapping = Task.Run(() => chain.FinalizeAt(last));
        bool creditedAgain = await pausedCredits.WaitAsync(TimeSpan.FromMilliseconds(500));
        resume.Set();
        await Task.WhenAll(slower, overlapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstCreditPaused, Is.True);
            Assert.That(creditedAgain, Is.False);
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(paused), Arg.Any<TxReceipt[]>());
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(last), Arg.Any<TxReceipt[]>());
        }
    }

    [Test]
    public void Long_gap_credits_only_its_latest_blocks()
    {
        const ulong firstNumber = 5;
        ulong lastNumber = firstNumber + 4 * FrameTxWidthFinalizer.MaxBlocksPerFinalization;
        using FinalizingChain chain = new(Enabled(), canonicalLength: lastNumber);
        Block skipped = chain.Canonical[lastNumber - FrameTxWidthFinalizer.MaxBlocksPerFinalization];
        Block oldestCredited = chain.Canonical[skipped.Number + 1];
        Block last = chain.Canonical[lastNumber];

        chain.FinalizeAt(chain.Canonical[firstNumber]);
        chain.FinalizeAt(last);

        using (Assert.EnterMultipleScope())
        {
            chain.Ledger.DidNotReceive().EarnWidthOnFinalization(Same(skipped), Arg.Any<TxReceipt[]>());
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(oldestCredited), Arg.Any<TxReceipt[]>());
            chain.Ledger.Received(1).EarnWidthOnFinalization(Same(last), Arg.Any<TxReceipt[]>());
        }
    }

    [Test]
    public void Reorged_out_block_earns_nothing()
    {
        using FinalizingChain chain = new(Enabled(), canonicalLength: 5);
        Block canonical = chain.Canonical[5];
        Block reorgedOut = FrameBlock(chain.Canonical[4], nonce: 1);
        chain.BlockTree.SuggestBlock(reorgedOut, BlockTreeSuggestOptions.ForceDontSetAsMain);

        chain.FinalizeAt(canonical);

        chain.Ledger.Received(1).EarnWidthOnFinalization(Same(canonical), Arg.Any<TxReceipt[]>());
        chain.Ledger.DidNotReceive().EarnWidthOnFinalization(Same(reorgedOut), Arg.Any<TxReceipt[]>());
    }

    [Test]
    public void Disabled_width_never_earns_on_finalization()
    {
        using FinalizingChain chain = new(new TxPoolConfig { FrameTxWidthEnabled = false }, canonicalLength: 5);

        chain.FinalizeAt(chain.Canonical[5]);

        chain.Ledger.DidNotReceiveWithAnyArgs().EarnWidthOnFinalization(default!, default!);
    }

    private static TxPoolConfig Enabled() => new() { FrameTxWidthEnabled = true };

    private static Block Same(Block block) => Arg.Is<Block>(found => found.Hash == block.Hash);

    private static Block FrameBlock(Block parent, ulong nonce = 0)
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.FrameTx)
            .WithNonce(nonce)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        return Build.A.Block.WithParent(parent).WithTransactions(tx).TestObject;
    }

    private sealed class FinalizingChain : IDisposable
    {
        private readonly IContainer _container;

        public FinalizingChain(TxPoolConfig txPoolConfig, ulong canonicalLength)
        {
            ReceiptFinder.Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns([new TxReceipt { GasUsed = GasUsed }]);
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(txPoolConfig))
                .AddModule(new BaseMergePluginModule())
                .AddSingleton(ReceiptFinder)
                .AddSingleton(Ledger)
                .AddSingleton<ITxPool>(_ => NullTxPool.Instance)
                .Build();
            _container.Resolve<ITxPool>();
            BlockTree = _container.Resolve<IBlockTree>();

            Canonical = new Block[canonicalLength + 1];
            Canonical[0] = Build.A.Block.Genesis.TestObject;
            BlockTreeBuilder.AddBlock(BlockTree, Canonical[0]);
            for (ulong number = 1; number <= canonicalLength; number++)
            {
                Canonical[number] = FrameBlock(Canonical[number - 1]);
                BlockTreeBuilder.AddBlock(BlockTree, Canonical[number]);
            }
        }

        public IReceiptFinder ReceiptFinder { get; } = Substitute.For<IReceiptFinder>();

        public IFrameTxWidthLedger Ledger { get; } = Substitute.For<IFrameTxWidthLedger>();

        public IBlockTree BlockTree { get; }

        public Block[] Canonical { get; }

        public void FinalizeAt(Block block) => BlockTree.ForkChoiceUpdated(block.Hash, block.Hash);

        public void Dispose() => _container.Dispose();
    }
}
