// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private const string NullResult = """{"jsonrpc":"2.0","result":null,"id":67}""";
    private const string MaxIndex = "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    /// <summary>An eth method that reads a block body, selecting the block by hash or by number, with its other arguments.</summary>
    public sealed record BlockBodyRead(string Method, bool ByHash, params object[] Arguments)
    {
        public object Selector(Block block) => ByHash ? block.Hash! : $"0x{block.Number:x}";

        public object?[] Parameters(object selector) => [selector, .. Arguments];

        public override string ToString() => $"{Method}({string.Join(", ", Arguments.Prepend(ByHash ? "hash" : "number"))})";
    }

    /// <summary>The thirteen eth methods that read a block body or its receipts, by hash and by number.</summary>
    private static IEnumerable<BlockBodyRead> BlockBodyReadsOneIndex() =>
    [
        new("eth_getBlockByHash", true, false),
        new("eth_getBlockByHash", true, true),
        new("eth_getBlockByNumber", false, false),
        new("eth_getBlockByNumber", false, true),
        new("eth_getBlockTransactionCountByHash", true),
        new("eth_getBlockTransactionCountByNumber", false),
        new("eth_getUncleCountByBlockHash", true),
        new("eth_getUncleCountByBlockNumber", false),
        new("eth_getTransactionByBlockHashAndIndex", true, "0x0"),
        new("eth_getTransactionByBlockNumberAndIndex", false, "0x0"),
        new("eth_getRawTransactionByBlockHashAndIndex", true, "0x0"),
        new("eth_getRawTransactionByBlockNumberAndIndex", false, "0x0"),
        new("eth_getUncleByBlockHashAndIndex", true, "0x0"),
        new("eth_getUncleByBlockNumberAndIndex", false, "0x0"),
        new("eth_getBlockReceipts", true),
        new("eth_getBlockReceipts", false),
    ];

    public static IEnumerable<TestCaseData> BlockBodyReads() =>
        BlockBodyReadsOneIndex().Select(static read => new TestCaseData(read).SetArgDisplayNames(read.ToString()));

    public static IEnumerable<TestCaseData> BlockBodyReadsByNumber() =>
        BlockBodyReads().Where(static data => data.Arguments[0] is BlockBodyRead { ByHash: false });

    /// <summary><see cref="BlockBodyReads"/>, plus the indexed reads with an index past the block's two transactions:
    /// a missing body cannot tell whether an index is in range.</summary>
    public static IEnumerable<TestCaseData> BlockBodyReadsAnyIndex() =>
        BlockBodyReadsOneIndex()
            .Concat(BlockBodyReadsOneIndex()
                .Where(static read => read.Arguments is ["0x0"])
                .SelectMany(static read => new[] { "0x2", MaxIndex }
                    .Select(index => read with { Arguments = [index] })))
            .Select(static read => new TestCaseData(read).SetArgDisplayNames(read.ToString()));

    [TestCaseSource(nameof(BlockBodyReadsAnyIndex))]
    public async Task Eth_block_read_of_a_missing_body_returns_pruned_history_only_below_the_served_floor(BlockBodyRead read)
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        Block block = await DeleteBodyBelowTheServedFloor(test);
        // During a backfill the published oldest block is below the served floor; the served floor decides.
        test.BlockTree.NewOldestBlock(1);
        object?[] parameters = read.Parameters(read.Selector(block));

        string belowTheFloor = await test.TestEthRpc(read.Method, parameters);
        test.BlockTree.UpdateLowestServedBlock(block.Number);
        string atTheFloor = await test.TestEthRpc(read.Method, parameters);
        test.BlockTree.UpdateLowestServedBlock(0);
        string aboveTheFloor = await test.TestEthRpc(read.Method, parameters);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(belowTheFloor, Is.EqualTo(PrunedHistory(read.ByHash ? block.Hash! : block.Number)));
            Assert.That(atTheFloor, Is.EqualTo(NullResult), "a missing body at the served floor is not pruned history");
            Assert.That(aboveTheFloor, Is.EqualTo(NullResult), "a missing body above the served floor is not pruned history");
        }
    }

    [TestCaseSource(nameof(BlockBodyReads))]
    public async Task Eth_block_read_of_an_unknown_hash_or_a_height_above_the_served_floor_returns_null(BlockBodyRead read)
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        await DeleteBodyBelowTheServedFloor(test);
        // An unknown hash has no height; this number is above both the head and the served floor.
        object selector = read.ByHash ? TestItem.KeccakA : "0xffffffff";

        string serialized = await test.TestEthRpc(read.Method, read.Parameters(selector));

        Assert.That(serialized, Is.EqualTo(NullResult));
    }

    [TestCaseSource(nameof(BlockBodyReadsByNumber))]
    public async Task Eth_block_read_by_number_of_a_headerless_height_below_the_served_floor_returns_4444(BlockBodyRead read)
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        // A floor above the head, as at startup: a height between them has no header and still reads as pruned history.
        ulong number = test.BlockTree.Head!.Number + 50;
        test.BlockTree.UpdateLowestServedBlock(number + 50);
        Assert.That(test.BlockTree.FindHeader(number), Is.Null, "precondition: no header at the requested height");

        string serialized = await test.TestEthRpc(read.Method, read.Parameters($"0x{number:x}"));

        Assert.That(serialized, Is.EqualTo(PrunedHistory(number)));
    }

    [Test]
    public async Task Eth_block_reads_of_a_body_kept_below_the_served_floor_serve_it([Values] bool byHash)
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        ulong number = await AddBlockWithTwoTransactionsFromB(test);
        Block block = test.BlockTree.FindBlock(number)!;
        Block empty = test.BlockTree.FindBlock(number + 1)!;
        test.BlockTree.UpdateLowestServedBlock(number + 2);
        EvictBlocks(test);
        Assert.That(test.BlockTree.LowestServedBlock, Is.EqualTo(number + 2), "precondition: both blocks are below the served floor");

        Task<string> Read(string byNumber, string byHashName, Block target, params object[] arguments) =>
            test.TestEthRpc(byHash ? byHashName : byNumber, [byHash ? (object)target.Hash! : $"0x{target.Number:x}", .. arguments]);

        string transactionCount = await Read("eth_getBlockTransactionCountByNumber", "eth_getBlockTransactionCountByHash", block);
        string emptyTransactionCount = await Read("eth_getBlockTransactionCountByNumber", "eth_getBlockTransactionCountByHash", empty);
        string uncleCount = await Read("eth_getUncleCountByBlockNumber", "eth_getUncleCountByBlockHash", block);
        string emptyReceipts = await Read("eth_getBlockReceipts", "eth_getBlockReceipts", empty);
        JObject receipts = JObject.Parse(await Read("eth_getBlockReceipts", "eth_getBlockReceipts", block));
        JObject hashesOnly = JObject.Parse(await Read("eth_getBlockByNumber", "eth_getBlockByHash", block, false));
        JObject full = JObject.Parse(await Read("eth_getBlockByNumber", "eth_getBlockByHash", block, true));
        JObject transaction = JObject.Parse(await Read("eth_getTransactionByBlockNumberAndIndex", "eth_getTransactionByBlockHashAndIndex", block, "0x1"));
        JObject rawTransaction = JObject.Parse(await Read("eth_getRawTransactionByBlockNumberAndIndex", "eth_getRawTransactionByBlockHashAndIndex", block, "0x1"));
        string transactionPastTheEnd = await Read("eth_getTransactionByBlockNumberAndIndex", "eth_getTransactionByBlockHashAndIndex", block, "0x2");
        string rawTransactionPastTheEnd = await Read("eth_getRawTransactionByBlockNumberAndIndex", "eth_getRawTransactionByBlockHashAndIndex", block, "0x2");
        string unclePastTheEnd = await Read("eth_getUncleByBlockNumberAndIndex", "eth_getUncleByBlockHashAndIndex", block, "0x0");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactionCount, Is.EqualTo("""{"jsonrpc":"2.0","result":"0x2","id":67}"""));
            Assert.That(emptyTransactionCount, Is.EqualTo("""{"jsonrpc":"2.0","result":"0x0","id":67}"""));
            Assert.That(uncleCount, Is.EqualTo("""{"jsonrpc":"2.0","result":"0x0","id":67}"""));
            Assert.That(emptyReceipts, Is.EqualTo("""{"jsonrpc":"2.0","result":[],"id":67}"""));
            Assert.That(receipts.SelectTokens("result[*].transactionHash").Select(static hash => (string?)hash),
                Is.EqualTo(block.Transactions.Select(static tx => tx.Hash!.ToString())));
            Assert.That(hashesOnly.SelectTokens("result.transactions[*]").Select(static hash => (string?)hash),
                Is.EqualTo(block.Transactions.Select(static tx => tx.Hash!.ToString())));
            Assert.That(full.SelectTokens("result.transactions[*].hash").Select(static hash => (string?)hash),
                Is.EqualTo(block.Transactions.Select(static tx => tx.Hash!.ToString())));
            Assert.That((string?)transaction.SelectToken("result.hash"), Is.EqualTo(block.Transactions[1].Hash!.ToString()));
            Assert.That((string?)rawTransaction["result"], Does.StartWith("0x").And.Length.GreaterThan(2));
            Assert.That(transactionPastTheEnd, Is.EqualTo(NullResult), "an index past the end of a kept body has no transaction");
            Assert.That(rawTransactionPastTheEnd, Is.EqualTo(NullResult), "an index past the end of a kept body has no raw transaction");
            Assert.That(unclePastTheEnd, Is.EqualTo(NullResult), "an index past the end of a kept body has no uncle");
        }
    }

    [Test]
    public async Task Eth_block_reads_keep_the_answer_for_canonical_hash_objects_and_unresolved_tags()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        Block block = await DeleteBodyBelowTheServedFloor(test);
        Block sideChain = Build.A.Block.WithParent(test.BlockTree.FindBlock(block.ParentHash!)!).WithExtraData([1]).TestObject;
        test.BlockTree.SuggestBlock(sideChain, BlockTreeSuggestOptions.ForceDontSetAsMain);
        Assert.That(test.BlockTree.IsMainChain(sideChain.Header), Is.False, "precondition: a known side-chain block below the served floor");

        (string Method, object[] Arguments)[] reads =
        [
            ("eth_getBlockByNumber", [false]),
            ("eth_getBlockTransactionCountByNumber", []),
            ("eth_getUncleCountByBlockNumber", []),
            ("eth_getTransactionByBlockNumberAndIndex", ["0x0"]),
            ("eth_getRawTransactionByBlockNumberAndIndex", ["0x0"]),
            ("eth_getUncleByBlockNumberAndIndex", ["0x0"]),
            ("eth_getBlockReceipts", []),
        ];

        using (Assert.EnterMultipleScope())
        {
            foreach ((string method, object[] arguments) in reads)
            {
                Assert.That(await test.TestEthRpc(method, [new BlockParameter(block.Hash!, requireCanonical: true), .. arguments]),
                    Is.EqualTo(PrunedHistory(block.Hash!)), $"{method}: a canonical hash requiring canonical is pruned history");
                Assert.That(await test.TestEthRpc(method, [new BlockParameter(sideChain.Hash!, requireCanonical: true), .. arguments]),
                    Is.EqualTo(NullResult), $"{method}: a side-chain hash requiring canonical");
                Assert.That(await test.TestEthRpc(method, ["safe", .. arguments]), Is.EqualTo(NullResult), $"{method}: an unresolved safe tag");
                Assert.That(await test.TestEthRpc(method, ["finalized", .. arguments]), Is.EqualTo(NullResult), $"{method}: an unresolved finalized tag");
            }
        }
    }

    [Test]
    public async Task Eth_get_transaction_receipt_of_a_block_pruned_below_the_served_floor_returns_4444([Values] bool hashValuedIndex)
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        Block block = await PruneBlockBelowTheServedFloor(test);
        Hash256 txHash = block.Transactions[1].Hash!;
        // With CompactTxIndex=false the entry is the block hash rather than its number.
        if (hashValuedIndex) TransactionIndex(test).Set(txHash, block.Hash!.BytesToArray());

        string belowTheFloor = await test.TestEthRpc("eth_getTransactionReceipt", txHash);
        test.BlockTree.UpdateLowestServedBlock(block.Number);
        string atTheFloor = await test.TestEthRpc("eth_getTransactionReceipt", txHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(belowTheFloor, Is.EqualTo(PrunedTransactionReceipt));
            Assert.That(atTheFloor, Is.EqualTo(NullResult), "a block missing at the served floor is not pruned history");
        }
    }

    [Test]
    public async Task Eth_get_transaction_receipt_without_a_canonical_header_below_the_served_floor_returns_null()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        Block block = await PruneBlockBelowTheServedFloor(test);
        Block sideChain = Build.A.Block.WithParent(test.BlockTree.FindBlock(block.ParentHash!)!)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject).WithExtraData([1]).TestObject;
        test.BlockTree.SuggestBlock(sideChain, BlockTreeSuggestOptions.ForceDontSetAsMain);
        test.Container.Resolve<IBlockStore>().Delete(sideChain.Number, sideChain.Hash!);
        Hash256 sideChainTx = sideChain.Transactions[0].Hash!;
        // Hash-valued entries, as CompactTxIndex=false writes them: one still names a reorganised-out block, one a hash
        // without a header.
        TransactionIndex(test).Set(sideChainTx, sideChain.Hash!.BytesToArray());
        TransactionIndex(test).Set(TestItem.KeccakB, TestItem.KeccakC.BytesToArray());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.BlockTree.IsMainChain(sideChain.Header), Is.False, "precondition: a side-chain block below the served floor");
            Assert.That(test.BlockTree.FindHeader(TestItem.KeccakC), Is.Null, "precondition: the second entry names no header");
        }

        string sideChainReceipt = await test.TestEthRpc("eth_getTransactionReceipt", sideChainTx);
        string headerlessReceipt = await test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakB);
        string unknownReceipt = await test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sideChainReceipt, Is.EqualTo(NullResult), "an entry naming a side-chain block");
            Assert.That(headerlessReceipt, Is.EqualTo(NullResult), "an entry naming a hash without a header");
            Assert.That(unknownReceipt, Is.EqualTo(NullResult), "a hash without an entry");
        }
    }

    [Test]
    public async Task Eth_get_transaction_receipt_of_a_block_kept_below_the_served_floor_is_unchanged()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain test = ctx.Test;
        ulong number = await AddBlockWithTwoTransactionsFromB(test);
        Hash256 txHash = test.BlockTree.FindBlock(number)!.Transactions[1].Hash!;
        string served = await test.TestEthRpc("eth_getTransactionReceipt", txHash);
        test.BlockTree.UpdateLowestServedBlock(number + 1);

        string keptBelowTheFloor = await test.TestEthRpc("eth_getTransactionReceipt", txHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)JObject.Parse(served).SelectToken("result.transactionHash"), Is.EqualTo(txHash.ToString()), "precondition: the receipt is served");
            Assert.That(keptBelowTheFloor, Is.EqualTo(served), "a receipt kept below the served floor");
        }
    }

    private const string PrunedTransactionReceipt = """{"jsonrpc":"2.0","error":{"code":4444,"message":"Pruned history unavailable"},"id":67}""";

    private static IDb TransactionIndex(TestRpcBlockchain test) => test.DbProvider.ReceiptsDb.GetColumnDb(ReceiptsColumns.Transactions);

    private static string PrunedHistory(object selector) =>
        $$"""{"jsonrpc":"2.0","error":{"code":4444,"message":"Pruned history unavailable for block {{selector}}"},"id":67}""";

    /// <summary>Adds a block with two transactions and one on top, sets the served floor above the first, and deletes its
    /// body, keeping its header and canonical level.</summary>
    private static async Task<Block> DeleteBodyBelowTheServedFloor(TestRpcBlockchain test)
    {
        ulong number = await AddBlockWithTwoTransactionsFromB(test);
        Block block = test.BlockTree.FindBlock(number)!;
        test.BlockTree.UpdateLowestServedBlock(number + 1);
        test.Container.Resolve<IBlockStore>().Delete(number, block.Hash!);
        EvictBlocks(test);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.BlockTree.LowestServedBlock, Is.EqualTo(number + 1), "precondition: the served floor is above the block");
            Assert.That(test.BlockTree.Head!.Number, Is.GreaterThan(number), "precondition: the head is above the block");
            Assert.That(test.BlockTree.FindHeader(block.Hash!), Is.Not.Null, "precondition: the header is kept");
            Assert.That(test.BlockTree.FindBlock(block.Hash!), Is.Null, "precondition: the body is gone");
            Assert.That(test.BlockTree.FindBlock(number), Is.Null, "precondition: the body is gone by number");
        }

        return block;
    }

    /// <summary><see cref="DeleteBodyBelowTheServedFloor"/>, then deletes the block's receipts too, as history pruning does,
    /// which leaves the transaction index to a later sweep.</summary>
    private static async Task<Block> PruneBlockBelowTheServedFloor(TestRpcBlockchain test)
    {
        Block block = await DeleteBodyBelowTheServedFloor(test);
        test.ReceiptStorage.RemoveReceipts(block.Number, block.Hash!);
        EvictBlocks(test);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(test.ReceiptStorage.HasBlock(block.Number, block.Hash!), Is.False, "precondition: the receipts are gone");
            Assert.That(test.ReceiptFinder.FindBlockHash(block.Transactions[1].Hash!), Is.EqualTo(block.Hash), "precondition: the index entry is kept");
        }

        return block;
    }
}
