// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Receipts;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Receipts;

[TestFixture]
public class ReceiptsRegenerationTests
{
    // Runtime returned by the creation init-codes is empty; only the constructor differs.
    // LOG0 over an empty payload, then RETURN nothing — a successful create that emits one log.
    private static readonly byte[] LogEmittingInitCode = [0x60, 0x00, 0x60, 0x00, 0xa0, 0x60, 0x00, 0x60, 0x00, 0xf3];
    // REVERT immediately — a create that fails, so its receipt carries status 0 and no contract address.
    private static readonly byte[] RevertingInitCode = [0x60, 0x00, 0x60, 0x00, 0xfd];

    public enum Scenario
    {
        LegacyTransfer,
        MultipleTransfers,
        ContractCreationWithLog,
        RevertingCreation,
        AccessListTx
    }

    private BasicTestBlockchain _chain = null!;
    private IShareableOverridableEnvSource<ReceiptsRegenerationEnv> _envSource = null!;
    private ReceiptsRegenerator _regenerator = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        // Berlin: EIP-658 (receipt status) and EIP-2718 (typed receipts) are active, but there is no base fee, so
        // simple transactions produce without gas-price juggling. The default mainnet provider would return
        // pre-Byzantium rules at these low block numbers, which the regenerator refuses.
        _chain = await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance)));

        RegeneratingReceiptsEnvSourceFactory factory = new(
            _chain.Container.Resolve<IOverridableEnvFactory>(),
            _chain.Container.Resolve<ILifetimeScope>(),
            [.. _chain.Container.Resolve<IEnumerable<IBlockValidationModule>>()]);
        _envSource = factory.Create(maxConcurrent: 2);
        _regenerator = new ReceiptsRegenerator(_envSource, _chain.BlockFinder, _chain.SpecProvider, _chain.EthereumEcdsa, LimboLogs.Instance);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _envSource?.Dispose();
        _chain?.Dispose();
    }

    [TestCase(Scenario.LegacyTransfer)]
    [TestCase(Scenario.MultipleTransfers)]
    [TestCase(Scenario.ContractCreationWithLog)]
    [TestCase(Scenario.RevertingCreation)]
    [TestCase(Scenario.AccessListTx)]
    public async Task Regenerated_receipts_match_the_stored_ones(Scenario scenario)
    {
        Block block = await AddBlock(BuildTransactions(scenario));
        TxReceipt[] stored = _chain.ReceiptStorage.Get(block);
        Assert.That(stored, Is.Not.Empty, "the scenario must produce at least one receipt");

        Assert.That(_regenerator.TryRegenerate(block, out TxReceipt[] regenerated), Is.True);
        AssertReceiptsMatch(stored, regenerated, block);
    }

    [TestCase(true, Description = "receipts on disk are served as-is, without re-execution")]
    [TestCase(false, Description = "an empty store falls back to regeneration")]
    public async Task Decorator_prefers_stored_receipts_then_regenerates(bool storedOnDisk)
    {
        Block block = await AddBlock(BuildTransactions(Scenario.ContractCreationWithLog));
        TxReceipt[] stored = _chain.ReceiptStorage.Get(block);

        IReceiptFinder inner = Substitute.For<IReceiptFinder>();
        inner.Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(storedOnDisk ? stored : []);
        RegeneratingReceiptFinder finder = new(inner, _chain.ReceiptStorage, _chain.BlockFinder, _regenerator);

        TxReceipt[] served = finder.Get(block);
        if (storedOnDisk)
            Assert.That(served, Is.SameAs(stored), "a populated store short-circuits before any re-execution");
        else
            AssertReceiptsMatch(stored, served, block);
    }

    [Test]
    public void Decorator_leaves_a_transactionless_block_alone()
    {
        Block genesis = _chain.BlockTree.FindBlock(0, BlockTreeLookupOptions.None)!;

        IReceiptFinder inner = Substitute.For<IReceiptFinder>();
        inner.Get(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns([]);
        RegeneratingReceiptFinder finder = new(inner, _chain.ReceiptStorage, _chain.BlockFinder, _regenerator);

        Assert.That(finder.Get(genesis), Is.Empty);
    }

    [Test]
    public void TryRegenerate_refuses_blocks_before_eip658()
    {
        // Pre-EIP-658 receipts carry a post-transaction state root a history-backed scope cannot reproduce, so the
        // guard must short-circuit before touching the env or block tree — hence the bare substitutes.
        ReceiptsRegenerator regenerator = new(
            Substitute.For<IShareableOverridableEnvSource<ReceiptsRegenerationEnv>>(),
            Substitute.For<IBlockFinder>(),
            new TestSpecProvider(Frontier.Instance),
            Substitute.For<IEthereumEcdsa>(),
            LimboLogs.Instance);

        Assert.That(regenerator.TryRegenerate(Build.A.Block.TestObject, out _), Is.False);
    }

    [Test]
    public async Task Wiring_decorates_the_receipt_finder_when_recovery_is_enabled()
    {
        using RecoverReceiptsBlockchain chain = await RecoverReceiptsBlockchain.Create();

        // Resolving the production graph with the flag on must yield the decorator, proving the module composes:
        // the env-source factory, the pooled source, the regenerator, and the IReceiptFinder decoration all resolve.
        Assert.That(chain.Container.ResolveKeyed<IReceiptFinder>(IReceiptFinder.RegenerableKey), Is.InstanceOf<RegeneratingReceiptFinder>());
    }

    // End to end through the production graph: the body is never persisted, yet a query still answers with the real
    // receipts, and the transaction index - which is what finds the block to regenerate from - survives.
    [Test]
    public async Task Deriving_from_state_serves_receipts_that_were_never_persisted()
    {
        using RecoverReceiptsBlockchain chain = await RecoverReceiptsBlockchain.Create();

        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.PrivateKeyA.Address);
        Transaction transfer = Build.A.Transaction
            .WithChainId(chain.SpecProvider.ChainId)
            .WithNonce(nonce)
            .WithTo(TestItem.AddressD)
            .WithValue(1)
            .WithGasPrice(1)
            .WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        Block block = await chain.AddBlock(transfer);

        IDb bodies = chain.Container.Resolve<IColumnsDb<ReceiptsColumns>>().GetColumnDb(ReceiptsColumns.Blocks);
        byte[] bodyKey = new byte[40];
        BinaryPrimitives.WriteUInt64BigEndian(bodyKey, block.Number);
        block.Hash!.Bytes.CopyTo(bodyKey.AsSpan(8));

        // The live storage caches every processed block, so reading through it would be served from memory and would
        // pass even with regeneration removed. A fresh instance over the same database has a cold cache.
        IReceiptFinder cold = ColdFinder(chain);
        TxReceipt[] served = cold.Get(block);
        bool gotIterator = cold.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator);
        int iterated = 0;
        using (iterator)
        {
            while (iterator.TryGetNext(out _)) iterated++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bodies.KeyExists(bodyKey), Is.False, "the body must never be persisted");
            Assert.That(served, Has.Length.EqualTo(1));
            Assert.That(ReceiptsRootCalculator.Instance.GetReceiptsRoot(served, chain.SpecProvider.GetSpec(block.Header), block.Header.ReceiptsRoot),
                Is.EqualTo(block.Header.ReceiptsRoot));
            Assert.That(gotIterator, Is.True, "eth_getLogs reads receipts through the iterator");
            Assert.That(iterated, Is.EqualTo(1), "the iterator must yield the regenerated receipts, not an empty span");
            Assert.That(chain.ReceiptStorage.FindBlockHash(transfer.Hash!), Is.EqualTo(block.Hash));
        }
    }

    // Consumers reach receipts through whatever the container hands them, and IReceiptStorage also satisfies
    // IReceiptFinder - so a consumer asking for the storage silently bypasses regeneration and the compiler cannot
    // catch it. Building the decorator by hand (ColdFinder) cannot detect that; this resolves the graph instead.
    [Test]
    public async Task Deriving_from_state_serves_receipts_through_the_resolved_eth_module()
    {
        using RecoverReceiptsBlockchain chain = await RecoverReceiptsBlockchain.Create();

        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.PrivateKeyA.Address);
        Transaction transfer = Build.A.Transaction
            .WithChainId(chain.SpecProvider.ChainId)
            .WithNonce(nonce)
            .WithTo(TestItem.AddressD)
            .WithValue(1)
            .WithGasPrice(1)
            .WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;

        Block block = await chain.AddBlock(transfer);
        // The live storage caches every processed block, so without a cold cache the read would be served from
        // memory and would pass even with regeneration bypassed - which is exactly the bug this pins.
        ((PersistentReceiptStorage)chain.Container.Resolve<IReceiptStorage>()).ClearCache();

        ResultWrapper<ReceiptForRpc[]> receipts = chain.Container.Resolve<EthModuleFactory>().Create()
            .eth_getBlockReceipts(new BlockParameter(block.Number));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipts.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(receipts.Data, Has.Length.EqualTo(1), "eth_getBlockReceipts must not report a derived block as receipt-less");
            Assert.That(receipts.Data[0].TransactionHash, Is.EqualTo(transfer.Hash));
        }
    }

    private static IReceiptFinder ColdFinder(RecoverReceiptsBlockchain chain)
    {
        IReceiptsRecovery recovery = chain.Container.Resolve<IReceiptsRecovery>();
        PersistentReceiptStorage storage = new(
            chain.Container.Resolve<IColumnsDb<ReceiptsColumns>>(),
            chain.SpecProvider,
            recovery,
            chain.BlockTree,
            chain.Container.Resolve<IBlockStore>(),
            chain.Container.Resolve<IReceiptConfig>());

        return new RegeneratingReceiptFinder(
            new FullInfoReceiptFinder(storage, recovery, chain.BlockFinder),
            storage,
            chain.BlockFinder,
            chain.Container.Resolve<ReceiptsRegenerator>());
    }

    private Task<Block> AddBlock(Transaction[] transactions) => _chain.AddBlock(transactions);

    private Transaction[] BuildTransactions(Scenario scenario)
    {
        ulong nonce = _chain.WorldStateManager.GlobalStateReader.GetNonce(_chain.BlockTree.Head!.Header, TestItem.PrivateKeyA.Address);
        return scenario switch
        {
            Scenario.LegacyTransfer => [Transfer(nonce)],
            Scenario.MultipleTransfers => [Transfer(nonce), Transfer(nonce + 1)],
            Scenario.ContractCreationWithLog => [Create(nonce, LogEmittingInitCode)],
            Scenario.RevertingCreation => [Create(nonce, RevertingInitCode)],
            Scenario.AccessListTx =>
            [
                Build.A.Transaction
                    .WithType(TxType.AccessList)
                    .WithChainId(_chain.SpecProvider.ChainId)
                    .WithNonce(nonce)
                    .WithTo(TestItem.AddressD)
                    .WithValue(1)
                    .WithGasPrice(1)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithAccessList(AccessList.Empty)
                    .SignedAndResolved(_chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject
            ],
            _ => throw new System.ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static Transaction Transfer(ulong nonce) => Build.A.Transaction
        .WithNonce(nonce)
        .WithTo(TestItem.AddressD)
        .WithValue(1)
        .WithGasLimit(GasCostOf.Transaction)
        .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static Transaction Create(ulong nonce, byte[] initCode) => Build.A.Transaction
        .WithNonce(nonce)
        .WithCode(initCode)
        .WithGasLimit(1_000_000)
        .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private void AssertReceiptsMatch(TxReceipt[] expected, TxReceipt[] actual, Block block)
    {
        IReceiptSpec spec = _chain.SpecProvider.GetSpec(block.Header);
        Hash256 expectedRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(expected, spec, block.Header.ReceiptsRoot);
        Hash256 actualRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(actual, spec, block.Header.ReceiptsRoot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Has.Length.EqualTo(expected.Length), "receipt count");
            Assert.That(actualRoot, Is.EqualTo(block.Header.ReceiptsRoot), "regenerated root matches the header");
            Assert.That(actualRoot, Is.EqualTo(expectedRoot), "regenerated root matches the stored one");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i].TxType, Is.EqualTo(expected[i].TxType), $"tx type [{i}]");
                Assert.That(actual[i].StatusCode, Is.EqualTo(expected[i].StatusCode), $"status [{i}]");
                Assert.That(actual[i].GasUsedTotal, Is.EqualTo(expected[i].GasUsedTotal), $"cumulative gas [{i}]");
                Assert.That(actual[i].GasUsed, Is.EqualTo(expected[i].GasUsed), $"gas used [{i}]");
                Assert.That(actual[i].Bloom, Is.EqualTo(expected[i].Bloom), $"bloom [{i}]");
                Assert.That(actual[i].ContractAddress, Is.EqualTo(expected[i].ContractAddress), $"contract address [{i}]");
                Assert.That(actual[i].Logs?.Length ?? 0, Is.EqualTo(expected[i].Logs?.Length ?? 0), $"log count [{i}]");
            }
        }
    }

    private sealed class RecoverReceiptsBlockchain : BasicTestBlockchain
    {
        public static async Task<RecoverReceiptsBlockchain> Create()
        {
            RecoverReceiptsBlockchain chain = new();
            await chain.Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance)));
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs() =>
            [.. base.CreateConfigs(), new ReceiptConfig { DeriveFromState = true }, new FlatDbConfig { Enabled = true, HistoryEnabled = true }];
    }
}
