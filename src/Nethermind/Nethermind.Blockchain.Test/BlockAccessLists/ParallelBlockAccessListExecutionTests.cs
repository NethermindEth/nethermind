// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

/// <summary>
/// Parallel BAL execution keeps one parent-state reader per tx processor for the whole block, so later transactions on a
/// processor read through a scope earlier ones already used. These blocks make the transactions share accounts and slots,
/// and check that parallel execution still matches sequential execution.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ParallelBlockAccessListExecutionTests
{
    private static readonly Address Counter = TestItem.AddressF;
    private static readonly Address Recipient = new("0x00000000000000000000000000000000000000ee");
    private static readonly UInt256 InitialCount = new(5ul);
    private static readonly UInt256 ReadOnlyValue = new(7ul);

    // Reads slot 1 (never written), then increments slot 0.
    private static readonly byte[] ReadAndIncrement = Bytes.FromHexString("0x6001545060005460010160005500");

    [Test]
    public async Task Parallel_execution_matches_sequential_execution_for_transactions_sharing_accounts_and_slots()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = true)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(Counter, UInt256.Zero);
                state.InsertCode(Counter, ReadAndIncrement, specProvider.GenesisSpec);
                state.Set(new StorageCell(Counter, UInt256.Zero), InitialCount);
                state.Set(new StorageCell(Counter, UInt256.One), ReadOnlyValue);
            }));

        // More transactions than pooled processors, so processors take later transactions and reuse their readers.
        int txCount = 2 * Environment.ProcessorCount + 2;
        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < txCount; i++)
        {
            PrivateKey sender = i % 2 == 0 ? TestItem.PrivateKeyB : TestItem.PrivateKeyC;
            ulong nonce = (ulong)(i / 2);
            transactions[i] = i % 3 == 2 ? Transfer(sender, nonce) : Call(sender, nonce);
        }
        int calls = transactions.Count(static tx => tx.To == Counter);

        Block block = await chain.AddBlock(transactions);
        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        ReadOnlyBlockAccessList bal = chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)
            ?? throw new AssertionException($"block {block.Number} has no stored block access list");
        IBlockAccessListManager balManager = ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>();
        IReleaseSpec spec = chain.SpecProvider.GetSpec(block.Header);
        TxReceipt[] produced = chain.ReceiptStorage.Get(block);

        (Block Block, TxReceipt[] Receipts, bool Parallel) parallel = Process(ProcessingOptions.ReadOnlyChain);
        (Block Block, TxReceipt[] Receipts, bool Parallel) sequential = Process(ProcessingOptions.ReadOnlyChain | ProcessingOptions.ForceSequentialBlockAccessList);
        // A second parallel run sets up the pool again, so it releases the readers kept for the first run.
        (Block Block, TxReceipt[] Receipts, bool Parallel) parallelAgain = Process(ProcessingOptions.ReadOnlyChain);

        chain.StateReader.GetStorage(block.Header, Counter, UInt256.Zero, out UInt256 count);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.Select(static r => r.StatusCode), Is.All.EqualTo(StatusCode.Success));
            Assert.That(count, Is.EqualTo(InitialCount + (UInt256)calls), "every call incremented the shared slot");
            Assert.That(parallel.Parallel, Is.True, "the first run took the parallel path");
            Assert.That(sequential.Parallel, Is.False, "the second run took the sequential path");
            Assert.That(parallelAgain.Parallel, Is.True, "the third run took the parallel path");

            foreach ((Block Block, TxReceipt[] Receipts, bool Parallel) run in new[] { parallel, sequential, parallelAgain })
            {
                Assert.That(run.Block.Header.StateRoot, Is.EqualTo(block.Header.StateRoot));
                Assert.That(run.Block.Header.BlockAccessListHash, Is.EqualTo(block.Header.BlockAccessListHash));
                Assert.That(run.Block.Header.GasUsed, Is.EqualTo(block.Header.GasUsed));
                Assert.That(Summarize(run.Receipts), Is.EqualTo(Summarize(produced)));
            }
        }

        (Block Block, TxReceipt[] Receipts, bool Parallel) Process(ProcessingOptions options)
        {
            Block suggested = block.WithReplacedHeader(block.Header.Clone());
            suggested.BlockAccessList = bal;
            using IDisposable scope = chain.MainWorldState.BeginScope(parent);
            (Block processed, TxReceipt[] receipts) = chain.BlockProcessor.ProcessOne(
                suggested, options, NullBlockTracer.Instance, spec, CancellationToken.None);
            return (processed, receipts, balManager.ParallelExecutionEnabled);
        }
    }

    private static (byte StatusCode, ulong GasUsed, ulong GasUsedTotal)[] Summarize(TxReceipt[] receipts) =>
        receipts.Select(static r => (r.StatusCode, r.GasUsed, r.GasUsedTotal)).ToArray();

    private static Transaction Call(PrivateKey sender, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(Counter).WithGasLimit(100_000)
            .SignedAndResolved(sender).TestObject;

    private static Transaction Transfer(PrivateKey sender, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(Recipient).WithValue(UInt256.One).WithGasLimit(1_000_000)
            .SignedAndResolved(sender).TestObject;
}
