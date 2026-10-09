// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.FFI.Test;

public class FfiNodeTests
{
    public enum Mutation
    {
        None,
        WrongStateRoot,
        WrongGasLimit,
        UnknownParent,
        Garbage,
    }

    [TestCase(Mutation.None, FfiStatus.Ok)]
    [TestCase(Mutation.WrongStateRoot, FfiStatus.InvalidBlock)]
    [TestCase(Mutation.WrongGasLimit, FfiStatus.InvalidBlock)]
    [TestCase(Mutation.UnknownParent, FfiStatus.ParentNotFound)]
    [TestCase(Mutation.Garbage, FfiStatus.DecodeError)]
    public async Task ExecuteBlock_validates_and_executes_without_persisting(Mutation mutation, FfiStatus expected)
    {
        using BasicTestBlockchain chain = await CreateChain(static _ => { });
        await chain.BuildSomeBlocks(2);
        using ILifetimeScope scope = FfiNode.CreateScope(chain.Container);
        FfiNode node = scope.Resolve<FfiNode>();
        Block head = chain.BlockTree.Head!;

        byte[] blockRlp = mutation is Mutation.Garbage ? Bytes.FromHexString("0xdeadbeef") : Rlp.Encode(Mutate(head, mutation)).Bytes;
        BlockExecutionResult result = node.ExecuteBlock(blockRlp);

        Assert.That(result.Status, Is.EqualTo(expected), result.Error);
        Assert.That(chain.BlockTree.Head, Is.SameAs(head));
        Assert.That(node.Head, Is.SameAs(head.Header));
        if (expected is not FfiStatus.Ok) return;

        RlpReader reader = new(result.ReceiptsRlp!);
        TxReceipt[] receipts = Rlp.GetDecoderOrThrow<TxReceipt>().DecodeArray(ref reader)!;
        Assert.That(result.Header!.Hash, Is.EqualTo(head.Hash));
        Assert.That(result.Header.StateRoot, Is.EqualTo(head.StateRoot));
        Assert.That(result.Header.GasUsed, Is.EqualTo(head.GasUsed));
        Assert.That(receipts, Has.Length.EqualTo(head.Transactions.Length));
        Assert.That(ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, chain.SpecProvider.GetSpec(head.Header), head.ReceiptsRoot),
            Is.EqualTo(head.ReceiptsRoot));
    }

    [Test]
    public async Task State_reads_serve_headers_accounts_storage_code_and_block_hashes()
    {
        using BasicTestBlockchain chain = await CreateChain(static _ => { });
        Block transferBlock = await chain.AddBlock(Transfer(chain, TestItem.PrivateKeyA, gasPrice: 10));
        Block head = await chain.AddBlock();
        using ILifetimeScope scope = FfiNode.CreateScope(chain.Container);
        FfiNode node = scope.Resolve<FfiNode>();

        Assert.That(node.OpenState(TestItem.KeccakA, out _), Is.EqualTo(FfiStatus.NotFound));
        Assert.That(node.OpenState(transferBlock.ParentHash!, out BlockHeader? parent), Is.EqualTo(FfiStatus.Ok));
        Assert.That(node.OpenState(head.Hash!, out BlockHeader? header), Is.EqualTo(FfiStatus.Ok));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.FindHeader(transferBlock.Hash!), Is.EqualTo(transferBlock.Header));
            Assert.That(node.FindHeader(TestItem.KeccakA), Is.Null);

            node.TryGetAccount(parent!, TestItem.AddressA, out AccountStruct senderBefore);
            Assert.That(node.TryGetAccount(header!, TestItem.AddressA, out AccountStruct sender), Is.True);
            Assert.That(sender.Nonce, Is.EqualTo(senderBefore.Nonce + 1));
            Assert.That(node.TryGetAccount(header!, TestItem.GetRandomAddress(), out _), Is.False);

            // The test genesis gives AddressA code and one storage slot.
            Assert.That(node.GetStorage(header!, TestItem.AddressA, UInt256.One), Is.EqualTo((UInt256)0xabcdef));
            Assert.That(node.GetStorage(header!, TestItem.AddressA, 2), Is.EqualTo(UInt256.Zero));
            Assert.That(node.GetCode(sender.CodeHash), Is.EqualTo(Bytes.FromHexString("0xabcd")));
            Assert.That(node.GetCode(Keccak.OfAnEmptyString.ValueHash256), Is.Empty);
            Assert.That(node.GetCode(TestItem.KeccakA.ValueHash256), Is.Null);

            Assert.That(node.GetBlockHash(header!, head.Number), Is.EqualTo(head.Hash));
            Assert.That(node.GetBlockHash(header!, transferBlock.Number), Is.EqualTo(transferBlock.Hash));
            Assert.That(node.GetBlockHash(header!, head.Number + 1), Is.Null);
        }
    }

    [TestCase("foundation.json")]
    [TestCase("sepolia.json")]
    [TestCase("hoodi.json")]
    public void Chain_config_round_trips_through_the_geth_genesis_loader(string chainSpecFile)
    {
        ChainSpec expected = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile(Path.Combine(TestContext.CurrentContext.TestDirectory, "chainspec", chainSpecFile));
        BlockHeader genesis = expected.Genesis!.Header;

        ChainSpec actual = new GethGenesisLoader(new EthereumJsonSerializer()).Load(new MemoryStream(GethGenesisWriter.Write(expected, genesis)));

        BlockHeader actualGenesis = actual.Genesis!.Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ChainId, Is.EqualTo(expected.ChainId));
            Assert.That(Forks(actual), Is.EqualTo(Forks(expected)));
            Assert.That(actual.TerminalTotalDifficulty, Is.EqualTo(expected.TerminalTotalDifficulty));
            Assert.That(actual.Parameters.DepositContractAddress, Is.EqualTo(expected.Parameters.DepositContractAddress));
            Assert.That(actual.Parameters.BlobSchedule, Is.EqualTo(expected.Parameters.BlobSchedule));
            Assert.That((actualGenesis.Nonce, actualGenesis.Timestamp, actualGenesis.GasLimit, actualGenesis.Difficulty, actualGenesis.MixHash, actualGenesis.Beneficiary, actualGenesis.BaseFeePerGas),
                Is.EqualTo((genesis.Nonce, genesis.Timestamp, genesis.GasLimit, genesis.Difficulty, genesis.MixHash, genesis.Beneficiary, genesis.BaseFeePerGas)));
            Assert.That(actualGenesis.ExtraData, Is.EqualTo(genesis.ExtraData));
        }

        // Constantinople is left out: Parity-style chain specs leave it unset when it activated together with Petersburg.
        static ulong?[] Forks(ChainSpec chainSpec) =>
        [
            chainSpec.HomesteadBlockNumber, chainSpec.DaoForkBlockNumber, chainSpec.TangerineWhistleBlockNumber, chainSpec.SpuriousDragonBlockNumber,
            chainSpec.ByzantiumBlockNumber, chainSpec.ConstantinopleFixBlockNumber, chainSpec.IstanbulBlockNumber, chainSpec.MuirGlacierNumber,
            chainSpec.BerlinBlockNumber, chainSpec.LondonBlockNumber, chainSpec.ArrowGlacierBlockNumber, chainSpec.GrayGlacierBlockNumber,
            chainSpec.MergeForkIdBlockNumber, chainSpec.ShanghaiTimestamp, chainSpec.CancunTimestamp, chainSpec.PragueTimestamp,
            chainSpec.OsakaTimestamp, chainSpec.AmsterdamTimestamp,
        ];
    }

    private static readonly ConcurrentQueue<(FfiTxEvent Event, Hash256 Hash, byte[] Tx)> TxEvents = new();

    [Test]
    public async Task Tx_callback_reports_pool_events_until_cleared()
    {
        TxEvents.Clear();
        using BasicTestBlockchain chain = await CreateChain(static builder =>
            builder.AddSingleton<ITxPoolConfig>(new TxPoolConfig { Size = 1 }));
        using ILifetimeScope scope = FfiNode.CreateScope(chain.Container);
        FfiNode node = scope.Resolve<FfiNode>();
        unsafe
        {
            node.SetTxCallback(&RecordTxEvent, 0);
            Assert.That(() => node.SetTxCallback(&RecordTxEvent, 1), Throws.InvalidOperationException);
        }

        // The pool holds one tx, so the better-paying one evicts the first before being included in the block.
        Transaction evicted = Transfer(chain, TestItem.PrivateKeyA, gasPrice: 1);
        Transaction included = Transfer(chain, TestItem.PrivateKeyB, gasPrice: 10);
        Assert.That(chain.TxPool.SubmitTx(evicted, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        await chain.AddBlock(included);

        (FfiTxEvent, Hash256)[] expected =
        [
            (FfiTxEvent.Pending, evicted.Hash!),
            (FfiTxEvent.Evicted, evicted.Hash!),
            (FfiTxEvent.Pending, included.Hash!),
            (FfiTxEvent.Removed, included.Hash!),
        ];
        Assert.That(() => TxEvents.Select(static e => (e.Event, e.Hash)), Is.EqualTo(expected).After(5000, 50));
        Assert.That(TxEvents.First().Tx, Is.EqualTo(TxDecoder.Instance.Encode(evicted, RlpBehaviors.SkipTypedWrapping).Bytes));

        unsafe
        {
            node.SetTxCallback(null, 0);
        }

        Assert.That(chain.TxPool.SubmitTx(Transfer(chain, TestItem.PrivateKeyA, gasPrice: 20), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(TxEvents, Has.Count.EqualTo(expected.Length));
    }

    private static readonly ConcurrentQueue<(FfiNodeEvent Event, ulong Number, Hash256 Hash)> NodeEvents = new();

    [Test]
    public async Task Node_callback_reports_head_finalization_and_sync_state_until_cleared()
    {
        NodeEvents.Clear();
        IEthSyncingInfo syncingInfo = Substitute.For<IEthSyncingInfo>();
        syncingInfo.IsSyncing().Returns(true);
        using BasicTestBlockchain chain = await CreateChain(builder => builder.AddSingleton(syncingInfo));
        using ILifetimeScope scope = FfiNode.CreateScope(chain.Container);
        FfiNode node = scope.Resolve<FfiNode>();
        BlockHeader genesis = chain.BlockTree.Head!.Header;
        unsafe
        {
            node.SetNodeCallback(&RecordNodeEvent, 0);
            Assert.That(() => node.SetNodeCallback(&RecordNodeEvent, 1), Throws.InvalidOperationException);
        }

        syncingInfo.IsSyncing().Returns(false);
        Block block = await chain.AddBlock();
        chain.BlockTree.ForkChoiceUpdated(block.Hash, block.Hash);

        (FfiNodeEvent, ulong, Hash256)[] expected =
        [
            (FfiNodeEvent.Syncing, genesis.Number, genesis.Hash!),
            (FfiNodeEvent.NewHead, block.Number, block.Hash!),
            (FfiNodeEvent.Synced, block.Number, block.Hash!),
            (FfiNodeEvent.Finalized, block.Number, block.Hash!),
        ];
        Assert.That(() => NodeEvents, Is.EqualTo(expected).After(5000, 50));

        unsafe
        {
            node.SetNodeCallback(null, 0);
        }

        await chain.AddBlock();
        Assert.That(NodeEvents, Has.Count.EqualTo(expected.Length));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void RecordNodeEvent(nint userData, NmNodeStatus* status) =>
        NodeEvents.Enqueue(((FfiNodeEvent)status->Event, status->Number, new Hash256(new ReadOnlySpan<byte>(status->Hash, Hash256.Size))));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void RecordTxEvent(nint userData, int txEvent, byte* hash, byte* tx, nuint txLength) =>
        TxEvents.Enqueue(((FfiTxEvent)txEvent, new Hash256(new ReadOnlySpan<byte>(hash, Hash256.Size)), new ReadOnlySpan<byte>(tx, (int)txLength).ToArray()));

    // FfiNode reads the sync state, which the test chain does not wire.
    private static Task<BasicTestBlockchain> CreateChain(Action<ContainerBuilder> configure) =>
        BasicTestBlockchain.Create(builder =>
        {
            builder
                .AddSingleton<ISyncModeSelector>(StaticSelector.Full)
                .AddSingleton(Substitute.For<IEthSyncingInfo>());
            configure(builder);
        });

    private static Transaction Transfer(BasicTestBlockchain chain, PrivateKey sender, ulong gasPrice) => Build.A.Transaction
        .WithTo(TestItem.AddressD)
        .WithGasPrice(gasPrice)
        .WithGasLimit(GasCostOf.Transaction)
        .SignedAndResolved(sender, chain.SpecProvider.GetSpec(chain.BlockTree.Head!.Header).IsEip155Enabled)
        .TestObject;

    private static Block Mutate(Block block, Mutation mutation)
    {
        BlockHeader header = block.Header.Clone();
        switch (mutation)
        {
            case Mutation.WrongStateRoot:
                header.StateRoot = TestItem.KeccakA;
                break;
            case Mutation.WrongGasLimit:
                header.GasLimit *= 2;
                break;
            case Mutation.UnknownParent:
                header.ParentHash = TestItem.KeccakB;
                break;
        }

        header.Hash = header.CalculateHash();
        return new Block(header, block.Body);
    }
}
