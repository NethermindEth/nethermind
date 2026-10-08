// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Test.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

[Parallelizable(ParallelScope.All)]
public class HashesOnlyBlockReaderTests
{
    private const int HeadWindow = 4;
    private const int ChainLength = 16;
    private const int HeadNumber = ChainLength - 1;

    public static IEnumerable<TestCaseData> StoredBlocks()
    {
        foreach ((string name, Block block) in BlockShapes())
        {
            yield return new TestCaseData(block, new HeaderDecoder(), new BlockForRpcFactory()).SetArgDisplayNames(name);
        }

        AuRaBlockHeader aura = AuRaBlockHeader.UpgradeFrom(Build.A.BlockHeader.WithNumber(5).WithDifficulty(3).WithTotalDifficulty(15L).TestObject);
        aura.AuRaStep = 42;
        aura.AuRaSignature = Enumerable.Repeat((byte)7, Signature.Size).ToArray();
        yield return new TestCaseData(new Block(aura, Build.A.Block.WithTransactions(LegacyTransactions(3)).TestObject.Body), new AuRaHeaderDecoder(), new AuRaBlockForRpcFactory())
            .SetArgDisplayNames("aura");
    }

    [TestCaseSource(nameof(StoredBlocks))]
    public void Read_StoredBlock_ServesTheResponseOfTheDecodedBlock(Block block, IHeaderDecoder headerDecoder, IBlockForRpcFactory factory)
    {
        MemDb blockDb = new();
        BlockStore blockStore = new(blockDb, headerDecoder);
        blockStore.Insert(block);
        Block decoded = blockStore.Get(block.Number, block.Hash!)!;
        decoded.Header.TotalDifficulty = block.TotalDifficulty;
        HashesOnlyBlockReader reader = new(blockDb, headerDecoder, HeadWindow);

        HashesOnlyBlock? hashesOnly = reader.Read(block.Number, block.Hash!);

        Assert.That(hashesOnly, Is.Not.Null, "the block is stored");
        hashesOnly!.Block.Header.TotalDifficulty = block.TotalDifficulty;
        Assert.That(Serialize(factory, hashesOnly), Is.EqualTo(Serialize(factory, decoded)),
            "the response must be byte-identical to the one built from the decoded block");
    }

    [Test]
    public void Read_StoredBlock_HashesEveryTransactionAsTheDecoder()
    {
        Block block = BlockShapes().First(static shape => shape.Name == "prague").Block;
        MemDb blockDb = new();
        new BlockStore(blockDb).Insert(block);
        HashesOnlyBlockReader reader = new(blockDb, new HeaderDecoder(), HeadWindow);

        HashesOnlyBlock hashesOnly = reader.Read(block.Number, block.Hash!)!;

        Assert.That(hashesOnly.TransactionHashes, Is.EqualTo(block.Transactions.Select(static tx => tx.CalculateHash()).ToArray()),
            "every transaction type hashes to its canonical hash");
    }

    [Test]
    public void Read_BlockMissingFromTheStore_ReturnsNull() =>
        Assert.That(new HashesOnlyBlockReader(new MemDb(), new HeaderDecoder(), HeadWindow).Read(1, TestItem.KeccakA), Is.Null,
            "a missing body is left to the block tree, which reports it");

    [TestCase(false, TestName = "Find_BlockBelowTheHeadWindow_ByNumber_ServesTheBlockTreeResponse")]
    [TestCase(true, TestName = "Find_BlockBelowTheHeadWindow_ByHash_ServesTheBlockTreeResponse")]
    public void Find_BlockBelowTheHeadWindow_ServesTheBlockTreeResponse(bool byHash)
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader) = BuildChain();
        BlockForRpcFactory factory = new();
        ulong windowStart = blockTree.Head!.Number - HeadWindow;
        Assert.That(windowStart, Is.EqualTo((ulong)(HeadNumber - HeadWindow)), "precondition: blocks below the window exist");

        for (ulong number = 1; number < windowStart; number++)
        {
            Block block = blockTree.FindBlock(number)!;
            BlockParameter parameter = byHash ? new BlockParameter(block.Hash!) : new BlockParameter(number);

            HashesOnlyBlock? hashesOnly = reader.Find(blockTree, parameter);

            Assert.That(hashesOnly, Is.Not.Null, $"block {number} is below the head window");
            Assert.That(Serialize(factory, hashesOnly!), Is.EqualTo(Serialize(factory, blockTree.FindBlock(parameter)!)),
                $"block {number} must be served as the block tree serves it");
        }
    }

    [TestCase(0UL, TestName = "Find_Genesis_ReturnsNull")]
    [TestCase((ulong)(HeadNumber - HeadWindow), TestName = "Find_FirstBlockOfTheHeadWindow_ReturnsNull")]
    [TestCase((ulong)HeadNumber, TestName = "Find_Head_ReturnsNull")]
    [TestCase((ulong)ChainLength, TestName = "Find_BlockAboveTheHead_ReturnsNull")]
    public void Find_BlockTheBlockTreeServes_ReturnsNull(ulong number)
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader) = BuildChain();
        Assert.That(blockTree.Head!.Number, Is.EqualTo((ulong)HeadNumber), "precondition: the chain has its expected head");

        Assert.That(reader.Find(blockTree, new BlockParameter(number)), Is.Null, $"block {number} is left to the block tree");
    }

    [Test]
    public void Find_ParameterOtherThanNumberOrHash_ReturnsNull()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader) = BuildChain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Find(blockTree, BlockParameter.Latest), Is.Null, "latest is left to the block tree");
            Assert.That(reader.Find(blockTree, BlockParameter.Earliest), Is.Null, "earliest is left to the block tree");
        }
    }

    [Test]
    public void Find_CanonicalHashRequiringCanonical_ServesTheBlock()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader) = BuildChain();
        Block block = blockTree.FindBlock(1)!;

        Assert.That(reader.Find(blockTree, new BlockParameter(block.Hash!, requireCanonical: true)), Is.Not.Null,
            "a canonical block passes the canonical check");
    }

    [Test]
    public void Find_RepeatedCanonicalBlock_IsServedFromTheCache()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader) = BuildChain();
        HashesOnlyBlock first = reader.Find(blockTree, new BlockParameter(2UL))!;
        Assert.That(first, Is.Not.Null, "precondition: block 2 is below the head window");

        HashesOnlyBlock? second = reader.Find(blockTree, new BlockParameter(first.Block.Hash!));

        Assert.That(second, Is.SameAs(first), "a canonical block is read once");
    }

    [Test]
    public void Find_CachedBlockBelowTheLowestServedBlock_ReadsTheStoreAgain()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader, IDb blockDb) = BuildChainWithDb();
        Block block = blockTree.FindBlock(2)!;
        Assert.That(reader.Find(blockTree, new BlockParameter(2UL)), Is.Not.Null, "precondition: block 2 is cached");

        blockTree.NewOldestBlock(3);
        blockDb.Delete(block.Number, block.Hash!);

        Assert.That(reader.Find(blockTree, new BlockParameter(2UL)), Is.Null, "a pruned body must not be served from the cache");
    }

    [Test]
    public void Find_BlockOffTheMainChain_IsNotCached()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader) = BuildChain();
        Block side = Build.A.Block.WithParent(blockTree.FindBlock(1)!).WithExtraData([0xAA]).WithTotalDifficulty(0L).TestObject;
        blockTree.SuggestBlock(side);
        Assert.That(blockTree.IsMainChain(side.Header), Is.False, "precondition: the block is on a side chain");

        HashesOnlyBlock? first = reader.Find(blockTree, new BlockParameter(side.Hash!));
        HashesOnlyBlock? second = reader.Find(blockTree, new BlockParameter(side.Hash!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null, "a side-chain block is served by hash");
            Assert.That(second, Is.Not.SameAs(first), "only canonical blocks are cached");
            Assert.That(reader.Find(blockTree, new BlockParameter(side.Hash!, requireCanonical: true)), Is.Null,
                "a side-chain block fails the canonical check, which the block tree reports");
        }
    }

    [Test]
    public async Task eth_getBlockByNumber_BlockBelowTheHeadWindow_ReturnsTheBlockTreeResponse()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(static builder =>
            builder.AddSingleton<HashesOnlyBlockReader, IDbProvider, IHeaderDecoder>(static (dbProvider, headerDecoder) =>
                new HashesOnlyBlockReader(dbProvider.BlocksDb, headerDecoder, headWindow: 0)));
        await chain.AddBlock(
            Build.A.Transaction.WithNonce(0).WithType(TxType.Legacy).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction.WithNonce(0).WithType(TxType.EIP1559).WithMaxFeePerGas(20.GWei).SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        await chain.AddBlock();
        HashesOnlyBlockReader reader = chain.Container.Resolve<HashesOnlyBlockReader>();
        BlockForRpcFactory factory = new();

        Assert.That(chain.BlockTree.Head!.Number, Is.GreaterThan(1UL), "precondition: blocks below the head exist");

        for (ulong number = 1; number < chain.BlockTree.Head!.Number; number++)
        {
            Block block = chain.BlockFinder.FindBlock(number)!;

            BlockForRpc response = chain.EthRpcModule.eth_getBlockByNumber(new BlockParameter(number), false).Data;

            // Only the module's own read can have cached the block once its body is gone from the store.
            chain.DbProvider.BlocksDb.Delete(block.Number, block.Hash!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(BlockForRpcWireFormatTests.Serialize(response),
                    Is.EqualTo(BlockForRpcWireFormatTests.Serialize(factory.Create(block, includeFullTransactionData: false, chain.SpecProvider))),
                    $"block {number} must be served as the block tree serves it");
                Assert.That(reader.Find(chain.BlockFinder, new BlockParameter(number)), Is.Not.Null,
                    $"block {number} must have been read through the reader");
            }
        }
    }

    private static (IBlockTree BlockTree, HashesOnlyBlockReader Reader) BuildChain()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader, _) = BuildChainWithDb();
        return (blockTree, reader);
    }

    private static (IBlockTree BlockTree, HashesOnlyBlockReader Reader, IDb BlockDb) BuildChainWithDb()
    {
        BlockTreeBuilder builder = Build.A.BlockTree()
            .WithTransactions(new InMemoryReceiptStorage())
            .OfChainLength(ChainLength);
        return (builder.TestObject, new HashesOnlyBlockReader(builder.BlocksDb, new HeaderDecoder(), HeadWindow), builder.BlocksDb);
    }

    private static string Serialize(IBlockForRpcFactory factory, HashesOnlyBlock hashesOnly)
    {
        BlockForRpc block = factory.Create(hashesOnly.Block, includeFullTransactionData: false, MainnetSpecProvider.Instance, skipTxs: true);
        block.Transactions = hashesOnly.TransactionHashes;
        return BlockForRpcWireFormatTests.Serialize(block);
    }

    private static string Serialize(IBlockForRpcFactory factory, Block block) =>
        BlockForRpcWireFormatTests.Serialize(factory.Create(block, includeFullTransactionData: false, MainnetSpecProvider.Instance));

    private static IEnumerable<(string Name, Block Block)> BlockShapes()
    {
        BlockHeader[] uncles =
        [
            Build.A.BlockHeader.WithNumber(1).WithExtraData([0xBB]).TestObject,
            Build.A.BlockHeader.WithNumber(1).WithExtraData([0xCC]).TestObject,
        ];

        yield return ("pre-merge", Build.A.Block.WithNumber(2).WithDifficulty(17).WithTotalDifficulty(34L)
            .WithTransactions(LegacyTransactions(4)).WithUncles(uncles).TestObject);
        yield return ("london", Build.A.Block.WithNumber(12_965_000).WithBaseFeePerGas(7).WithTransactions(BlockForRpcWireFormatTests.AllTypes()[..4]).TestObject);
        yield return ("shanghai", Build.A.Block.WithNumber(17_034_870).WithTimestamp(1_681_338_455).WithBaseFeePerGas(7)
            .WithTransactions(LegacyTransactions(2)).WithWithdrawals(16).TestObject);
        yield return ("cancun", Build.A.Block.WithNumber(25_000_000).WithTimestamp(1_710_338_135).WithBaseFeePerGas(7)
            .WithTransactions(BlockForRpcWireFormatTests.AllTypes()).WithWithdrawals(2).WithBlobGasUsed(262144).WithExcessBlobGas(1)
            .WithParentBeaconBlockRoot(TestItem.KeccakE).TestObject);
        yield return ("prague", Build.A.Block.WithNumber(25_000_001).WithTimestamp(1_800_000_000).WithBaseFeePerGas(3)
            .WithTransactions([.. BlockForRpcWireFormatTests.AllTypes(), FrameTransaction(), LargeTransaction()])
            .WithWithdrawals(8).WithBlobGasUsed(1).WithExcessBlobGas(2).WithParentBeaconBlockRoot(TestItem.KeccakA)
            .WithRequestsHash(TestItem.KeccakB).WithBlockAccessListHash(TestItem.KeccakC).WithSlotNumber(7).TestObject);
        yield return ("empty", Build.A.Block.WithNumber(3).WithTransactions([]).WithWithdrawals([]).TestObject);
    }

    private static Transaction[] LegacyTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (uint i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithNonce(i).WithData([(byte)i])
                .Signed(new EthereumEcdsa(TestBlockchainIds.ChainId), TestItem.PrivateKeyA, true).TestObject;
        }

        return transactions;
    }

    private static Transaction FrameTransaction()
    {
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = BlockchainIds.Mainnet,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, default, default)],
            FrameSignatures = [],
        };
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    // Above the decoder's delayed-hash limit, so the decoder hashes it eagerly.
    private static Transaction LargeTransaction() =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithData(new byte[40_000]).SignedAndResolved().TestObject;
}
