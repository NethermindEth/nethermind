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
using Nethermind.Blockchain.Headers;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

[Parallelizable(ParallelScope.All)]
public class HashesOnlyBlockReaderTests
{
    private const int HeadWindow = 4;
    private const int ChainLength = 16;
    private const int HeadNumber = ChainLength - 1;
    private const long CacheBudget = HashesOnlyBlockCache.DefaultByteBudget;

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
        HashesOnlyBlockReader reader = StandaloneReader(blockDb, headerDecoder);

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
        HashesOnlyBlockReader reader = StandaloneReader(blockDb, new HeaderDecoder());

        HashesOnlyBlock hashesOnly = reader.Read(block.Number, block.Hash!)!;

        Assert.That(hashesOnly.TransactionHashes, Is.EqualTo(block.Transactions.Select(static tx => tx.CalculateHash().ValueHash256).ToArray()),
            "every transaction type hashes to its canonical hash");
    }

    [Test]
    public void Find_StoredBlockWithAnUnknownBodyField_IsLeftToTheBlockTree()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(ChainLength, withWithdrawals: true);
        IBlockTree blockTree = builder.TestObject;
        IDb blockDb = builder.BlocksDb;
        HashesOnlyBlockReader reader = CreateReader(builder);
        Block block = blockTree.FindBlock(2)!;
        Assert.That(block.Withdrawals, Is.Not.Null, "precondition: the extra field follows the withdrawals");
        byte[] storedKey = new byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(block.Number, block.Hash!, storedKey);
        blockDb.Set(block.Number, block.Hash!, WithTrailingBodyField(blockDb.Get(storedKey)!));

        HashesOnlyBlock? first = null;
        Assert.DoesNotThrow(() => first = reader.Find(blockTree, new BlockParameter(2UL)), "a layout the reader does not know must not fail the request");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Null, "the block is left to the block tree's decoder");
            Assert.That(reader.Read(block.Number, block.Hash!), Is.Null, "nothing is read, so nothing is cached");
        }
    }

    [Test]
    public void Read_StoredBlockOverTheUncleLimit_ReturnsNull()
    {
        Block block = Build.A.Block.WithNumber(2).WithUncles(
            Build.A.BlockHeader.WithExtraData([0xAA]).TestObject,
            Build.A.BlockHeader.WithExtraData([0xBB]).TestObject,
            Build.A.BlockHeader.WithExtraData([0xCC]).TestObject).TestObject;
        MemDb blockDb = new();
        BlockStore blockStore = new(blockDb);
        blockStore.Insert(block);
        Assert.Throws<RlpLimitException>(() => blockStore.Get(block.Number, block.Hash!), "precondition: the block decoder rejects three uncles");

        Assert.That(StandaloneReader(blockDb, new HeaderDecoder()).Read(block.Number, block.Hash!), Is.Null,
            "the reader keeps the decoder's limits and leaves the block to it");
    }

    [Test]
    public void Read_BlockMissingFromTheStore_ReturnsNull() =>
        Assert.That(StandaloneReader(new MemDb(), new HeaderDecoder()).Read(1, TestItem.KeccakA), Is.Null,
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
    public void Find_BlocksAcrossTheMerge_ServeTheBlockTreeTotalDifficulty()
    {
        // Genesis and blocks 1-4 have difficulty, block 5 is the terminal block, blocks 6-12 are post-merge.
        Block[] blocks = new Block[13];
        blocks[0] = Build.A.Block.Genesis.WithDifficulty(1).WithTotalDifficulty(1L).TestObject;
        for (int i = 1; i < blocks.Length; i++)
        {
            blocks[i] = Build.A.Block.WithParent(blocks[i - 1]).WithDifficulty(i <= 5 ? 10UL : 0UL).TestObject;
        }

        BlockTreeBuilder builder = Build.A.BlockTree().WithBlocks(blocks);
        IBlockTree blockTree = builder.TestObject;
        HashesOnlyBlockReader reader = CreateReader(builder);
        TestSpecProvider specProvider = new(London.Instance);
        BlockForRpcFactory factory = new();

        for (ulong number = 1; number < blockTree.Head!.Number - HeadWindow; number++)
        {
            string expected = BlockForRpcWireFormatTests.Serialize(factory.Create(blockTree.FindBlock(new BlockParameter(number))!, false, specProvider));
            Assert.That(expected, Does.Contain("\"totalDifficulty\""), $"precondition: block {number} reports its total difficulty");

            HashesOnlyBlock miss = reader.Find(blockTree, new BlockParameter(number))!;
            HashesOnlyBlock hit = reader.Find(blockTree, new BlockParameter(number))!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hit, Is.SameAs(miss), $"precondition: block {number} is served from the cache the second time");
                Assert.That(Serialize(factory, miss, specProvider), Is.EqualTo(expected), $"block {number} read from the store");
                Assert.That(Serialize(factory, hit, specProvider), Is.EqualTo(expected), $"block {number} served from the cache");
            }
        }
    }

    [TestCase(false, false, 1, TestName = "Find_ByNumber_ReadsOneChainLevelOnAMissAndOnAHit")]
    [TestCase(true, false, 0, TestName = "Find_ByHash_ReadsOneChainLevelOnAMissAndNoneOnAHit")]
    [TestCase(true, true, 1, TestName = "Find_ByHashRequiringCanonical_ReadsOneChainLevelOnAMissAndOnAHit")]
    public void Find_Block_ReadsTheExpectedChainLevels(bool byHash, bool requireCanonical, int hitLoads)
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader, CountingChainLevels chainLevels) = BuildCountingChain();
        Block block = blockTree.FindBlock(2)!;
        BlockParameter parameter = byHash ? new BlockParameter(block.Hash!, requireCanonical) : new BlockParameter(2UL);
        chainLevels.Loads = 0;

        HashesOnlyBlock first = reader.Find(blockTree, parameter)!;
        int missLoads = chainLevels.Loads;
        chainLevels.Loads = 0;
        HashesOnlyBlock? second = reader.Find(blockTree, parameter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null, "precondition: block 2 is below the head window");
            Assert.That(second, Is.SameAs(first), "precondition: the block is served from the cache");
            Assert.That(missLoads, Is.EqualTo(1), "a miss resolves the block with one level read");
            Assert.That(chainLevels.Loads, Is.EqualTo(hitLoads), "only resolving a number or the canonical check reads a level on a hit");
        }
    }

    [Test]
    public void Find_LevelStoringNoTotalDifficulty_ServesTheBlockTreeResponseFromTheCache()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader, CountingChainLevels chainLevels, BlockTreeBuilder builder) = BuildCountingChainWithBuilder();
        Block block = blockTree.FindBlock(2)!;
        SetTotalDifficulty(chainLevels, block, UInt256.Zero);
        ((IClearableCache)builder.BlockStore).ClearCache();
        ((IClearableCache)builder.HeaderStore).ClearCache();
        Block decoded = blockTree.FindBlock(new BlockParameter(block.Hash!))!;
        Assert.That(decoded.TotalDifficulty, Is.Null, "precondition: the block tree leaves an unknown total difficulty unset");
        TestSpecProvider specProvider = new(London.Instance);
        BlockForRpcFactory factory = new();
        string expected = BlockForRpcWireFormatTests.Serialize(factory.Create(decoded, false, specProvider));

        HashesOnlyBlock miss = reader.Find(blockTree, new BlockParameter(2UL))!;
        chainLevels.Loads = 0;
        HashesOnlyBlock? hit = reader.Find(blockTree, new BlockParameter(2UL));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hit, Is.SameAs(miss), "a level storing no total difficulty must still be served from the cache");
            Assert.That(chainLevels.Loads, Is.EqualTo(1), "the hit costs only the number's level read");
            Assert.That(Serialize(factory, miss, specProvider), Is.EqualTo(expected), "the response is the block tree's");
        }
    }

    [Test]
    public void Find_CachedBlockWhoseTotalDifficultyChanged_IsReplacedForBothLookups()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader, CountingChainLevels chainLevels) = BuildCountingChain();
        Block block = blockTree.FindBlock(2)!;
        HashesOnlyBlock stale = reader.Find(blockTree, new BlockParameter(2UL))!;
        UInt256 totalDifficulty = stale.Block.TotalDifficulty!.Value + 1;
        SetTotalDifficulty(chainLevels, block, totalDifficulty);

        HashesOnlyBlock fresh = reader.Find(blockTree, new BlockParameter(2UL))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fresh, Is.Not.SameAs(stale), "a changed total difficulty is read again");
            Assert.That(fresh.Block.TotalDifficulty, Is.EqualTo(totalDifficulty), "the new total difficulty is served");
            Assert.That(reader.Find(blockTree, new BlockParameter(2UL)), Is.SameAs(fresh), "the fresh block replaces the stale one");
            Assert.That(reader.Find(blockTree, new BlockParameter(block.Hash!)), Is.SameAs(fresh), "a lookup by hash serves the same block");
        }
    }

    [Test]
    public void Find_CachedBlockOfADeletedChainSlice_ReturnsNull()
    {
        BlockTreeBuilder builder = Build.A.BlockTree().WithTransactions(new InMemoryReceiptStorage()).OfChainLength(ChainLength);
        IBlockTree blockTree = builder.TestObject;
        HashesOnlyBlockReader reader = CreateReader(builder, headWindow: 0);
        Hash256 blockHash = blockTree.FindBlock(2)!.Hash!;
        Assert.That(reader.Find(blockTree, new BlockParameter(blockHash)), Is.Not.Null, "precondition: block 2 is cached");

        blockTree.DeleteChainSlice(2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockTree.FindBlock(new BlockParameter(blockHash)), Is.Null, "precondition: the block tree no longer has the block");
            Assert.That(reader.Find(blockTree, new BlockParameter(blockHash)), Is.Null, "a deleted block must not be served from the cache");
        }
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
            builder.AddSingleton<HashesOnlyBlockReader, IDbProvider, IHeaderDecoder, IChainLevelInfoRepository, IHeaderStore, IBlockTree, ISpecProvider>(
                static (dbProvider, headerDecoder, chainLevels, headerStore, blockTree, specProvider) => new HashesOnlyBlockReader(
                    dbProvider.BlocksDb, headerDecoder, chainLevels, headerStore, blockTree, specProvider, headWindow: 0, CacheBudget)));
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
        return (builder.TestObject, CreateReader(builder), builder.BlocksDb);
    }

    private static (IBlockTree BlockTree, HashesOnlyBlockReader Reader, CountingChainLevels ChainLevels) BuildCountingChain()
    {
        (IBlockTree blockTree, HashesOnlyBlockReader reader, CountingChainLevels chainLevels, _) = BuildCountingChainWithBuilder();
        return (blockTree, reader, chainLevels);
    }

    private static (IBlockTree BlockTree, HashesOnlyBlockReader Reader, CountingChainLevels ChainLevels, BlockTreeBuilder Builder) BuildCountingChainWithBuilder()
    {
        CountingChainLevels chainLevels = new(new ChainLevelInfoRepository(new MemDb()));
        BlockTreeBuilder builder = Build.A.BlockTree().WithChainLevelInfoRepository(chainLevels)
            .WithTransactions(new InMemoryReceiptStorage()).OfChainLength(ChainLength);
        return (builder.TestObject, CreateReader(builder), chainLevels, builder);
    }

    private static void SetTotalDifficulty(IChainLevelInfoRepository chainLevels, Block block, UInt256 totalDifficulty)
    {
        ChainLevelInfo level = chainLevels.LoadLevel(block.Number)!;
        level.FindBlockInfo(block.Hash!)!.TotalDifficulty = totalDifficulty;
        chainLevels.PersistLevel(block.Number, level);
    }

    private static HashesOnlyBlockReader CreateReader(BlockTreeBuilder builder, ulong headWindow = HeadWindow) =>
        new(builder.BlocksDb, new HeaderDecoder(), builder.ChainLevelInfoRepository, builder.HeaderStore, builder.TestObject,
            MainnetSpecProvider.Instance, headWindow, CacheBudget);

    private static HashesOnlyBlockReader StandaloneReader(IDb blockDb, IHeaderDecoder headerDecoder) =>
        new(blockDb, headerDecoder, Substitute.For<IChainLevelInfoRepository>(), Substitute.For<IHeaderStore>(),
            Substitute.For<IBlockTree>(), MainnetSpecProvider.Instance, HeadWindow, CacheBudget);

    // A later fork's body field: one more item after the withdrawals.
    private static byte[] WithTrailingBodyField(byte[] storedBlock)
    {
        RlpReader reader = new(storedBlock);
        int end = reader.ReadSequenceLength() + reader.Position;
        List<Rlp> items = [];
        while (reader.Position < end)
        {
            int length = reader.PeekNextRlpLength();
            items.Add(new Rlp(reader.Peek(length).ToArray()));
            reader.SkipBytes(length);
        }

        items.Add(Rlp.OfEmptyList);
        return Rlp.Encode([.. items]).Bytes;
    }

    private static string Serialize(IBlockForRpcFactory factory, HashesOnlyBlock hashesOnly) =>
        Serialize(factory, hashesOnly, MainnetSpecProvider.Instance);

    private static string Serialize(IBlockForRpcFactory factory, HashesOnlyBlock hashesOnly, ISpecProvider specProvider)
    {
        BlockForRpc block = factory.Create(hashesOnly.Block, includeFullTransactionData: false, specProvider, skipTxs: true);
        block.Transactions = BlockTransactions.FromHashes(hashesOnly.TransactionHashes);
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

    private sealed class CountingChainLevels(IChainLevelInfoRepository inner) : IChainLevelInfoRepository
    {
        public int Loads { get; set; }

        public ChainLevelInfo? LoadLevel(ulong number)
        {
            Loads++;
            return inner.LoadLevel(number);
        }

        public void Delete(ulong number, BatchWrite? batch = null) => inner.Delete(number, batch);

        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null) => inner.PersistLevel(number, level, batch);

        public BatchWrite StartBatch() => inner.StartBatch();

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers) => inner.MultiLoadLevel(in blockNumbers);
    }
}
