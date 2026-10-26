// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Blockchain.Find;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class MainProcessingModuleTests
{
    [Test]
    public async Task Main_processing_retains_receipts_when_disk_storage_is_disabled()
    {
        using TransientReceiptsBlockchain chain = new();
        await chain.Initialize();
        Assert.That(chain.ReceiptStorage, Is.TypeOf<TransientReceiptStorage>());
        await chain.BuildSomeBlocks(1);
        Block block = chain.BlockTree.Head!;
        Assert.That(chain.ReceiptStorage.Get(block), Has.Length.EqualTo(1));
        Assert.That(chain.ReceiptStorage.FindBlockHash(block.Transactions[0].Hash!), Is.EqualTo(block.Hash));
    }

    private sealed class TransientReceiptsBlockchain : BasicTestBlockchain
    {
        protected override IEnumerable<IConfig> CreateConfigs() =>
            [.. base.CreateConfigs(), new ReceiptConfig { StoreReceipts = false }];

        public Task Initialize() => Build();
    }

    [Test]
    public void Receipt_storage_disabled_keeps_canonical_polling_filters_working()
    {
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ReceiptConfig { StoreReceipts = false }))
            .Build();
        Assert.That(((MainProcessingContext)ctx.Resolve<IMainProcessingContext>()).LifetimeScope.Resolve<BlockchainProcessor.Options>().StoreReceiptsByDefault, Is.True);
        IReceiptStorage storage = ctx.Resolve<IReceiptStorage>();
        Assert.That(storage, Is.TypeOf<TransientReceiptStorage>());
        IBlockTree tree = ctx.Resolve<IBlockTree>();
        FilterManager filters = ctx.Resolve<FilterManager>();
        FilterStore filterStore = ctx.Resolve<FilterStore>();
        LogFilter logs = new(0, new(BlockParameterType.Earliest), new(BlockParameterType.Latest), AddressFilter.AnyAddress, new SequenceTopicsFilter());
        BlockFilter hashes = new(1);
        filterStore.SaveFilter(logs);
        filterStore.SaveFilter(hashes);
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        LogEntry log = new(TestItem.AddressA, [], []);
        Block genesis = Build.A.Block.Genesis.TestObject;
        Block block = Build.A.Block.WithParent(genesis).WithNumber(1).WithTransactions(tx).WithBloom(new Bloom([log])).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithTransactionHash(tx.Hash).WithLogs(log).TestObject;
        tree.SuggestBlock(genesis);
        tree.TryUpdateMainChain(genesis.Header, true);
        storage.Insert(block, [receipt], ensureCanonical: false);
        tree.SuggestBlock(block);
        using ManualResetEventSlim notified = new();
        using ManualResetEventSlim emptyNotified = new();
        ctx.Resolve<IReceiptMonitor>().ReceiptsInserted += (_, e) =>
        {
            if (e.BlockHeader.Hash == block.Hash && !e.WasRemoved) notified.Set();
            if (e.BlockHeader.Number == 2 && !e.WasRemoved) emptyNotified.Set();
        };
        Assert.That(tree.TryUpdateMainChain(block.Header, true), Is.True);
        Assert.That(notified.Wait(TimeSpan.FromSeconds(10)), Is.True);
        using Nethermind.Core.Collections.ArrayPoolList<FilterLog> foundLogs = filters.PollLogs(logs.Id);
        using Nethermind.Core.Collections.ArrayPoolList<Hash256> foundHashes = filters.PollBlockHashes(hashes.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundLogs, Has.Count.EqualTo(1));
            Assert.That(foundLogs[0].BlockHash, Is.EqualTo(block.Hash));
            Assert.That(foundHashes, Does.Contain(block.Hash));
        }

        Block empty = Build.A.Block.WithParent(block).WithNumber(2).TestObject;
        storage.Insert(empty, [], ensureCanonical: false);
        tree.SuggestBlock(empty);
        Assert.That(tree.TryUpdateMainChain(empty.Header, true), Is.True);
        Assert.That(emptyNotified.Wait(TimeSpan.FromSeconds(10)), Is.True);
        using Nethermind.Core.Collections.ArrayPoolList<Hash256> emptyHashes = filters.PollBlockHashes(hashes.Id);
        Assert.That(emptyHashes, Does.Contain(empty.Hash));
        tree.ForkChoiceUpdated(block.Hash, block.Hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.HasBlock(block.Number, block.Hash!), Is.False);
            Assert.That(storage.HasBlock(empty.Number, empty.Hash!), Is.True);
        }
    }

    [TestCase(32768, true)]
    [TestCase(0, false)]
    [TestCase(-1, false)]
    public void MainProcessingContext_ShouldUseCachedCodeInfoRepository_OnlyWithAPrecompileCacheBudget(int maxKilobytes, bool expectDecorated)
    {
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig { PrecompileCacheMaxKilobytes = maxKilobytes }))
            .Build();

        ICodeInfoRepository repository = (ctx.Resolve<IMainProcessingContext>() as MainProcessingContext)
            .LifetimeScope
            .Resolve<ICodeInfoRepository>();

        Assert.That(repository is PrecompileCachedCodeInfoRepository, Is.EqualTo(expectDecorated), $"resolved {repository.GetType().Name}");
    }

    [Test]
    public void MainProcessingContext_ShouldKeepBlockCode_UntilThePreWarmerClearsCaches()
    {
        StaticCodeCache rootCodeCache = new(MemoryAllowance.CodeCacheSize);
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig()))
            .AddSingleton<ICodeCache>(rootCodeCache)
            .Build();

        ILifetimeScope mainScope = (ctx.Resolve<IMainProcessingContext>() as MainProcessingContext).LifetimeScope;
        BlockCodeCache blockCodeCache = mainScope.Resolve<BlockCodeCache>();
        byte[] bytecode = Guid.NewGuid().ToByteArray();
        ValueHash256 codeHash = ValueKeccak.Compute(bytecode);
        CodeInfo code = new(bytecode);
        blockCodeCache.Set(in codeHash, code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootCodeCache.Get(in codeHash), Is.SameAs(code), "wraps the root's cache");
            Assert.That(mainScope.Resolve<ICodeCache>(), Is.SameAs(blockCodeCache));
            Assert.That(ctx.Resolve<ICodeCache>(), Is.SameAs(rootCodeCache), "outside block processing");
            Assert.That(mainScope.Resolve<TransactionProcessorAdapterFactory>()(mainScope.Resolve<ITransactionProcessor>()),
                Is.InstanceOf<BlockCodeCacheTxAdapter>(), "adapters parallel execution builds");
        }

        // Leaves the block's copy as the only one.
        rootCodeCache.Clear();
        Assert.That(blockCodeCache.Get(in codeHash), Is.SameAs(code));

        mainScope.Resolve<IBlockCachePreWarmer>().ClearCaches();
        Assert.That(blockCodeCache.Get(in codeHash), Is.Null);
    }
}
