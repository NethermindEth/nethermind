// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using static Nethermind.LightClient.Test.ExecutionProofFixtures;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedRpcTests
{
    private static readonly Address AccountAddress = new("0x1234567890123456789012345678901234567890");
    private static readonly Hash256 BlockHash = Keccak.Compute("verified block");

    [TestCase("eth_getBalance", "0x7b")]
    [TestCase("eth_getTransactionCount", "0x7")]
    public async Task Account_values_are_derived_from_the_proof(string method, string expected)
    {
        using Provider provider = new(new Account(7, 123));
        object actual = await provider.Rpc.InvokeAsync(method, Parameters(AccountAddress.ToString(), "finalized"), CancellationToken.None);

        Assert.That(actual, Is.EqualTo(expected));
        AssertFinalizedRequest(provider.Requests[0], "account");
    }

    [Test]
    public async Task Storage_value_is_derived_from_proof_and_padded_to_32_bytes(
        [Values("0x1", "0x01", "0x0000000000000000000000000000000000000000000000000000000000000001")] string slotText)
    {
        UInt256 slot = 1;
        byte[] storageLeaf = Leaf(Keccak.Compute(slot.ToBigEndian()), Rlp.Encode((UInt256)42).Bytes);
        using Provider provider = new(new Account(7, 123, Keccak.Compute(storageLeaf), Keccak.OfAnEmptyString), storageLeaf);

        object actual = await provider.Rpc.InvokeAsync("eth_getStorageAt", Parameters(AccountAddress.ToString(), slotText, "finalized"), CancellationToken.None);

        Assert.That(actual, Is.EqualTo("0x" + new string('0', 62) + "2a"));
        AssertFinalizedRequest(provider.Requests[1], "storage");
    }

    [Test]
    public async Task Absence_returns_authenticated_zero([Values("eth_getBalance", "eth_getTransactionCount", "eth_getCode", "eth_getStorageAt")] string method)
    {
        using Provider provider = new(new Account(123));
        object[] parameters = method == "eth_getStorageAt" ? [Address.Zero.ToString(), "0x1", "finalized"] : [Address.Zero.ToString(), "finalized"];

        object actual = await provider.Rpc.InvokeAsync(method, Parameters(parameters), CancellationToken.None);

        string expected = method switch
        {
            "eth_getCode" => "0x",
            "eth_getStorageAt" => "0x" + new string('0', 64),
            _ => "0x0"
        };
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task Code_must_match_authenticated_account([Values] bool tamper)
    {
        byte[] code = [0x60, 0x01, 0x00];
        using Provider provider = new(new Account(7, 123, Keccak.EmptyTreeHash, Keccak.Compute(code)), code: tamper ? [0x60, 0x02, 0x00] : code);

        if (tamper)
            await AssertRpcErrorAsync(provider, "eth_getCode", Parameters(AccountAddress.ToString(), "finalized"), -32000);
        else
            Assert.That(await provider.Rpc.InvokeAsync("eth_getCode", Parameters(AccountAddress.ToString(), "finalized"), CancellationToken.None), Is.EqualTo("0x600100"));
        Assert.That(provider.Requests[1].Kind, Is.EqualTo("code"));
    }

    [Test]
    public async Task Forged_account_proof_is_rejected([Values] bool omit)
    {
        using Provider provider = new(new Account(7, 123)) { OmitProof = omit, TamperProof = !omit };

        await AssertRpcErrorAsync(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), -32000);
    }

    [Test]
    public async Task Storage_rejects_a_tampered_proof()
    {
        byte[] storageLeaf = Leaf(Keccak.Compute(((UInt256)1).ToBigEndian()), Rlp.Encode((UInt256)42).Bytes);
        using Provider provider = new(new Account(7, 123, Keccak.Compute(storageLeaf), Keccak.OfAnEmptyString), storageLeaf)
        {
            TamperStorageProof = true
        };

        await AssertRpcErrorAsync(provider, "eth_getStorageAt", Parameters(AccountAddress.ToString(), "0x1", "finalized"), -32000);
    }

    [Test]
    public async Task Unverified_selectors_are_rejected_before_network([Values("pending", "safe", "earliest", "0x9", "0x0a")] string selector)
    {
        using Provider provider = new(new Account(7, 123));
        RpcException? exception = await Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), selector), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Code, Is.AnyOf(-32602, -32001));
            Assert.That(provider.Requests, Is.Empty);
        }
    }

    [Test]
    public async Task Latest_uses_the_authenticated_optimistic_head()
    {
        using Provider provider = new(new Account(7, 123));
        provider.LatestHead = new VerifiedHead(101, 11, BlockHash, provider.Head.StateRoot);

        Assert.That(await provider.Rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None), Is.EqualTo("0xb"));
        Assert.That(await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), "latest"), CancellationToken.None), Is.EqualTo("0x7b"));
        Assert.That(provider.LastQueriedHead!.Number, Is.EqualTo(11));
    }

    [Test]
    public async Task Latest_without_an_authenticated_optimistic_head_errors()
    {
        using Provider provider = new(new Account(7, 123));
        using VerifiedRpc rpc = new(provider, () => provider.Head, 1);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo(-32001));
    }

    [Test]
    public async Task Syncing_reports_retained_head_freshness(
        [Values(99UL, 100UL, 110UL, 111UL, 4101UL)] ulong currentSlot, [Values] bool omittedParameters)
    {
        using Provider provider = new(new Account(7, 123)) { CurrentSlot = currentSlot };
        provider.LatestHead = provider.Head with { Number = 20 };

        object status = await provider.Rpc.InvokeAsync("eth_syncing", omittedParameters ? default : Parameters(), CancellationToken.None);

        if (currentSlot is >= 100 and <= 110)
            Assert.That(status, Is.EqualTo(false));
        else
            AssertSyncingStatus(status, "0xa", "0x14");
        AssertNoExecutionRequests(provider);
    }

    [Test]
    public async Task Syncing_reports_a_missing_head_without_network([Values] bool missingAtConstruction)
    {
        using Provider provider = new(new Account(7, 123));
        if (missingAtConstruction) provider.LatestHead = null;
        using VerifiedRpc rpc = provider.CreateRpc();
        provider.LatestHead = null;

        object status = await rpc.InvokeAsync("eth_syncing", Parameters(), CancellationToken.None);

        AssertSyncingStatus(status, missingAtConstruction ? "0x0" : "0xa", "0x0");
        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
            await rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo(-32001));
        AssertNoExecutionRequests(provider);
    }

    [Test]
    public async Task Syncing_and_block_number_do_not_read_stale_finality()
    {
        using Provider provider = new(new Account(7, 123)) { CurrentSlot = 4101 };
        int finalizedReads = 0;
        using VerifiedRpc rpc = provider.CreateRpc(getHead: () =>
        {
            finalizedReads++;
            throw new RpcException(-32000, "Finality is stale.");
        });
        provider.LatestHead = provider.Head with { Number = 20 };

        object status = await rpc.InvokeAsync("eth_syncing", Parameters(), CancellationToken.None);
        object blockNumber = await rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None);

        AssertSyncingStatus(status, "0xa", "0x14");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockNumber, Is.EqualTo("0x14"));
            Assert.That(finalizedReads, Is.Zero);
        }
        AssertNoExecutionRequests(provider);
    }

    [Test]
    public async Task Syncing_rejects_invalid_parameters([Values("[1]", "{}", "null")] string json)
    {
        using Provider provider = new(new Account(7, 123));
        using JsonDocument parameters = JsonDocument.Parse(json);

        await AssertRpcErrorAsync(provider, "eth_syncing", parameters.RootElement, -32602);

        AssertNoExecutionRequests(provider);
    }

    [Test]
    public async Task Block_number_retains_a_stale_authenticated_height([Values(111UL, 4101UL)] ulong currentSlot)
    {
        using Provider provider = new(new Account(7, 123)) { CurrentSlot = currentSlot };
        provider.LatestHead = provider.Head with { Number = 20 };

        object number = await provider.Rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None);

        Assert.That(number, Is.EqualTo("0x14"));
        AssertNoExecutionRequests(provider);
    }

    [TestCaseSource(nameof(LatestAliasRequests))]
    public async Task Stale_latest_aliases_reject_before_execution(string method, JsonElement parameters)
    {
        using PrivateKey? key = method == "eth_sendRawTransaction"
            ? new("0x0000000000000000000000000000000000000000000000000000000000000001") : null;
        using Provider provider = new(new Account(7, 1_000_000_000),
            headNumber: key is null ? 10UL : 20_000_000UL, accountAddress: key?.Address)
        { CurrentSlot = 111 };
        using VerifiedCall? calls = method is "eth_call" or "eth_estimateGas"
            ? new(provider, MainnetSpecProvider.Instance, NullLogManager.Instance) : null;
        using VerifiedRpc rpc = provider.CreateRpc(calls);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
            await rpc.InvokeAsync(method, parameters, CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(-32001));
        AssertNoExecutionRequests(provider);
    }

    [TestCaseSource(nameof(PinnedDataRequests))]
    public async Task Explicit_block_data_and_calls_remain_available_while_latest_is_stale(string method, JsonElement parameters)
    {
        using Provider provider = new(new Account(7, 123)) { CurrentSlot = 111 };
        using VerifiedCall? calls = method is "eth_call" or "eth_estimateGas"
            ? new(provider, MainnetSpecProvider.Instance, NullLogManager.Instance) : null;
        using VerifiedRpc rpc = provider.CreateRpc(calls);

        object result = await rpc.InvokeAsync(method, parameters, CancellationToken.None);

        switch (method)
        {
            case "eth_getBlockByNumber":
            case "eth_getBlockByHash":
                JsonElement block = (JsonElement)result;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(BlockHash.ToString()));
                    Assert.That(block.GetProperty("number").GetString(), Is.EqualTo("0xa"));
                }
                break;
            case "eth_getBlockTransactionCountByNumber":
            case "eth_getBlockTransactionCountByHash":
                Assert.That(((JsonElement)result).GetString(), Is.EqualTo("0x0"));
                break;
            case "eth_getTransactionByBlockNumberAndIndex":
            case "eth_getTransactionByBlockHashAndIndex":
                Assert.That(((JsonElement)result).ValueKind, Is.EqualTo(JsonValueKind.Null));
                break;
            case "eth_getBlockReceipts":
            case "eth_getLogs":
                Assert.That(((JsonElement)result).GetArrayLength(), Is.Zero);
                break;
            case "eth_feeHistory":
                Assert.That(((JsonElement)result).GetProperty("oldestBlock").GetString(), Is.EqualTo("0xa"));
                break;
            case "eth_call":
                Assert.That(result, Is.EqualTo("0x"));
                break;
            case "eth_estimateGas":
                Assert.That(result, Is.EqualTo("0x5208"));
                break;
            default:
                Assert.Fail($"Missing expected result for {method}.");
                break;
        }
    }

    [Test]
    public async Task Latest_recovers_when_an_authenticated_slot_advances_at_the_same_execution_height()
    {
        using Provider provider = new(new Account(7, 123)) { CurrentSlot = 111 };
        AssertSyncingStatus(await provider.Rpc.InvokeAsync("eth_syncing", Parameters(), CancellationToken.None), "0xa", "0xa");
        await AssertRpcErrorAsync(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), "latest"), -32001);
        AssertNoExecutionRequests(provider);

        VerifiedHead recovered = provider.Head with { Slot = 111 };
        provider.LatestHead = recovered;

        object status = await provider.Rpc.InvokeAsync("eth_syncing", Parameters(), CancellationToken.None);
        object balance = await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), "latest"), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(false));
            Assert.That(balance, Is.EqualTo("0x7b"));
            Assert.That(provider.LastQueriedHead, Is.EqualTo(recovered));
            Assert.That(recovered.Number, Is.EqualTo(provider.Head.Number));
            Assert.That(recovered.BlockHash, Is.EqualTo(provider.Head.BlockHash));
        }
    }

    private static IEnumerable<TestCaseData> LatestAliasRequests()
    {
        foreach (string method in new[] { "eth_getBalance", "eth_getTransactionCount", "eth_getCode" })
            yield return new(method, Parameters(AccountAddress.ToString(), "latest"));
        yield return new("eth_getStorageAt", Parameters(AccountAddress.ToString(), "0x0", "latest"));
        yield return new("eth_getProof", Parameters(AccountAddress.ToString(), Array.Empty<string>(), "latest"));
        yield return new("eth_gasPrice", Parameters());
        yield return new("eth_maxPriorityFeePerGas", Parameters());
        yield return new("eth_feeHistory", Parameters("0x1", "latest", Array.Empty<double>()));
        object call = new { from = AccountAddress.ToString(), to = Address.Zero.ToString(), gas = "0x186a0" };
        foreach (string method in new[] { "eth_call", "eth_estimateGas" })
        {
            yield return new(method, Parameters(call));
            yield return new(method, Parameters(call, "latest"));
        }
        yield return new("eth_getBlockByNumber", Parameters("latest", false));
        yield return new("eth_getBlockTransactionCountByNumber", Parameters("latest"));
        yield return new("eth_getTransactionByBlockNumberAndIndex", Parameters("latest", "0x0"));
        yield return new("eth_getBlockReceipts", Parameters("latest"));
        foreach (object filter in new object[]
        {
            new { }, new { fromBlock = "latest" }, new { toBlock = "latest" },
            new { fromBlock = "latest", toBlock = "latest" },
            new { fromBlock = "latest", toBlock = "0xa" }, new { fromBlock = "0xa", toBlock = "latest" },
        })
            yield return new("eth_getLogs", Parameters(filter));
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction transaction = new() { Nonce = 7, GasLimit = 21_000, GasPrice = 2, To = AccountAddress, Value = 3 };
        new EthereumEcdsa(1).Sign(key, transaction, true);
        byte[] encoded = TxDecoder.Instance.EncodeAsBytes(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        yield return new("eth_sendRawTransaction", Parameters("0x" + Convert.ToHexString(encoded).ToLowerInvariant()));
    }

    private static IEnumerable<TestCaseData> PinnedDataRequests()
    {
        yield return new("eth_getBlockByNumber", Parameters("0xa", false));
        yield return new("eth_getBlockByHash", Parameters(BlockHash.ToString(), false));
        yield return new("eth_getBlockTransactionCountByNumber", Parameters("0xa"));
        yield return new("eth_getBlockTransactionCountByHash", Parameters(BlockHash.ToString()));
        yield return new("eth_getTransactionByBlockNumberAndIndex", Parameters("0xa", "0x0"));
        yield return new("eth_getTransactionByBlockHashAndIndex", Parameters(BlockHash.ToString(), "0x0"));
        yield return new("eth_getBlockReceipts", Parameters("0xa"));
        yield return new("eth_getLogs", Parameters(new { fromBlock = "0xa", toBlock = "0xa" }));
        yield return new("eth_getLogs", Parameters(new { blockHash = BlockHash.ToString() }));
        yield return new("eth_feeHistory", Parameters("0x1", "0xa", Array.Empty<double>()));
        object call = new { from = AccountAddress.ToString(), to = Address.Zero.ToString(), gas = "0x186a0" };
        foreach (string method in new[] { "eth_call", "eth_estimateGas" })
            yield return new(method, Parameters(call, "0xa"));
    }

    private static void AssertSyncingStatus(object status, string startingBlock, string currentBlock)
    {
        JsonElement result = JsonSerializer.SerializeToElement(status);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(result.EnumerateObject().Select(static property => property.Name),
            Is.EquivalentTo(new[] { "startingBlock", "currentBlock", "highestBlock" }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("startingBlock").GetString(), Is.EqualTo(startingBlock));
            Assert.That(result.GetProperty("currentBlock").GetString(), Is.EqualTo(currentBlock));
            Assert.That(result.GetProperty("highestBlock").GetString(), Is.EqualTo(currentBlock));
        }
    }

    private static void AssertNoExecutionRequests(Provider provider)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Requests, Is.Empty);
            Assert.That(provider.LastQueriedHead, Is.Null);
            Assert.That(provider.HeaderByHashFetches, Is.Zero);
            Assert.That(provider.CanonicalHeaderFetches, Is.Zero);
            Assert.That(provider.BlockFetches, Is.Zero);
            Assert.That(provider.ReceiptFetches, Is.Zero);
            Assert.That(provider.Broadcasts, Is.Empty);
        }
    }

    [Test]
    public async Task Stale_finality_does_not_block_exact_authenticated_optimistic_reads()
    {
        using Provider provider = new(new Account(7, 123));
        int finalizedReads = 0;
        using VerifiedRpc rpc = new(provider, () =>
        {
            finalizedReads++;
            throw new RpcException(-32000, "Finality is stale.");
        }, 1, specProvider: MainnetSpecProvider.Instance, getLatestHead: () => provider.Head);

        object[] selectors = ["latest", "0xa", BlockHash.ToString(),
            new { blockNumber = "0xa" }, new { blockHash = BlockHash.ToString(), requireCanonical = true }];
        foreach (object selector in selectors)
            Assert.That(await rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), selector), CancellationToken.None),
                Is.EqualTo("0x7b"), $"state selector {selector}");

        JsonElement byNumber = (JsonElement)await rpc.InvokeAsync("eth_getBlockByNumber", Parameters("0xa", false), CancellationToken.None);
        JsonElement byHash = (JsonElement)await rpc.InvokeAsync("eth_getBlockByHash", Parameters(BlockHash.ToString(), false), CancellationToken.None);
        object[] logFilters = [new { }, new { fromBlock = "0xa", toBlock = "0xa" },
            new { fromBlock = "latest", toBlock = "0xa" }, new { fromBlock = "0xa", toBlock = "latest" }];
        foreach (object filter in logFilters)
        {
            JsonElement logs = (JsonElement)await rpc.InvokeAsync("eth_getLogs", Parameters(filter), CancellationToken.None);
            Assert.That(logs.GetArrayLength(), Is.Zero, $"log filter {filter}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byNumber.GetProperty("hash").GetString(), Is.EqualTo(BlockHash.ToString()));
            Assert.That(byHash.GetProperty("hash").GetString(), Is.EqualTo(BlockHash.ToString()));
            Assert.That(finalizedReads, Is.Zero);
        }
    }

    [Test]
    public async Task Stale_finality_rejects_explicit_finalized_aliases_before_execution(
        [Values("eth_getBalance", "eth_getBlockByNumber", "eth_getLogs", "eth_getProof", "eth_feeHistory")] string method)
    {
        using Provider provider = new(new Account(7, 123));
        using VerifiedRpc rpc = new(provider, () => provider.Head, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => provider.Head, isFinalizedHeadFresh: _ => false);
        JsonElement parameters = method switch
        {
            "eth_getBalance" => Parameters(AccountAddress.ToString(), "finalized"),
            "eth_getBlockByNumber" => Parameters("finalized", false),
            "eth_getLogs" => Parameters(new { fromBlock = "finalized", toBlock = "latest" }),
            "eth_getProof" => Parameters(AccountAddress.ToString(), Array.Empty<string>(), "finalized"),
            _ => Parameters("0x1", "finalized", Array.Empty<int>()),
        };

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method, parameters, CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(-32000));
        AssertNoExecutionRequests(provider);
    }

    [Test]
    public async Task Stale_finality_keeps_authenticated_pinned_state_ancestors_available(
        [Values("eth_getBalance", "eth_getCode", "eth_getProof")] string method, [Values(0, 1, 2, 3)] int selectorKind,
        [Values] bool optimistic)
    {
        using Provider provider = optimistic ? OptimisticProvider() : new(new Account(7, 123), headNumber: 300);
        if (!optimistic)
            provider.HistoricalHeader = Provider.CreateHeader(299, Keccak.Compute("retained finalized ancestor"), provider.Head.StateRoot);
        BlockHeader header = provider.HistoricalHeader!;
        object selector = selectorKind switch
        {
            0 => VerifiedRpc.Quantity(header.Number),
            1 => new { blockNumber = VerifiedRpc.Quantity(header.Number) },
            2 => header.Hash!.ToString(),
            _ => new { blockHash = header.Hash!.ToString(), requireCanonical = true },
        };
        using VerifiedRpc rpc = new(provider, () => provider.Head, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => provider.LatestHead, isFinalizedHeadFresh: _ => false);
        JsonElement parameters = method == "eth_getProof"
            ? Parameters(AccountAddress.ToString(), Array.Empty<string>(), selector) : Parameters(AccountAddress.ToString(), selector);

        object result = await rpc.InvokeAsync(method, parameters, CancellationToken.None);

        if (method == "eth_getProof")
        {
            JsonElement proof = (JsonElement)result;
            byte[][] witness = proof.GetProperty("accountProof").EnumerateArray().Select(item => Convert.FromHexString(item.GetString()!.AsSpan(2))).ToArray();
            Assert.That(ExecutionProofVerifier.VerifyAccount(header.StateRoot!, AccountAddress, witness).Balance, Is.EqualTo((UInt256)123));
        }
        else Assert.That(result, Is.EqualTo(method == "eth_getBalance" ? "0x7b" : optimistic ? "0x602a00" : "0x"));
        Assert.That(provider.LastCanonicalAnchor, Is.EqualTo(optimistic ? provider.LatestHead : provider.Head));
        Assert.That(provider.LastQueriedHead!.BlockHash, Is.EqualTo(header.Hash));
    }

    [Test]
    public async Task Stale_finality_keeps_authenticated_block_ancestors_available([Values] bool byHash, [Values] bool optimistic)
    {
        using Provider provider = optimistic ? OptimisticProvider() : new(new Account(7, 123), headNumber: 300);
        if (!optimistic)
            provider.HistoricalHeader = Provider.CreateHeader(299, Keccak.Compute("retained finalized block"), provider.Head.StateRoot);
        BlockHeader header = provider.HistoricalHeader!;
        using VerifiedRpc rpc = new(provider, () => provider.Head, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => provider.LatestHead, isFinalizedHeadFresh: _ => false);

        JsonElement block = (JsonElement)await rpc.InvokeAsync(byHash ? "eth_getBlockByHash" : "eth_getBlockByNumber",
            Parameters(byHash ? header.Hash!.ToString() : VerifiedRpc.Quantity(header.Number), false), CancellationToken.None);

        Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(header.Hash!.ToString()));
        Assert.That(provider.LastCanonicalAnchor, Is.EqualTo(optimistic ? provider.LatestHead : provider.Head));
    }

    [Test]
    public async Task Stale_finality_keeps_authenticated_log_ranges_available([Values] bool optimistic)
    {
        LogRangeSource source = new();
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325), isFinalizedHeadFresh: _ => false);
        ulong last = optimistic ? 325UL : 320UL;

        JsonElement logs = (JsonElement)await rpc.InvokeAsync("eth_getLogs",
            Parameters(new { fromBlock = "0x13f", toBlock = VerifiedRpc.Quantity(last), address = AccountAddress.ToString() }), CancellationToken.None);

        AssertRangeLogs(logs, source, 319, last);
    }

    [Test]
    public async Task Stale_finality_keeps_finalized_transaction_search_available(
        [Values("eth_getTransactionByHash", "eth_getTransactionReceipt")] string method)
    {
        LogRangeSource source = new(signTransactions: true);
        int finalizedReads = 0;
        using VerifiedRpc rpc = new(source, () => { finalizedReads++; return source.Head(300); }, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325), isFinalizedHeadFresh: _ => false);
        string hash = source[300].Transactions[1].Hash!.ToString();

        JsonElement transaction = (JsonElement)await rpc.InvokeAsync(method, Parameters(hash), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.GetProperty(method == "eth_getTransactionByHash" ? "hash" : "transactionHash").GetString(), Is.EqualTo(hash));
            Assert.That(transaction.GetProperty("blockHash").GetString(), Is.EqualTo(source.Head(300).BlockHash.ToString()));
            Assert.That(transaction.GetProperty("blockNumber").GetString(), Is.EqualTo(VerifiedRpc.Quantity(300)));
            Assert.That(finalizedReads, Is.EqualTo(1));
        }
    }

    [TestCase("finalized", 300UL, 320UL)]
    [TestCase("crossing", 320UL, 325UL)]
    [TestCase("numeric-latest", 319UL, 325UL)]
    [TestCase("optimistic", 321UL, 324UL)]
    [TestCase("omitted-to", 320UL, 325UL)]
    [TestCase("below-anchor", 300UL, 314UL)]
    public async Task Recent_log_ranges_follow_one_authenticated_chain(string scenario, ulong first, ulong last)
    {
        LogRangeSource source = new();
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        object filter = scenario switch
        {
            "finalized" => new { fromBlock = VerifiedRpc.Quantity(first), toBlock = "finalized", address = AccountAddress.ToString() },
            "crossing" => new { fromBlock = "finalized", toBlock = "latest", address = AccountAddress.ToString() },
            "numeric-latest" => new { fromBlock = VerifiedRpc.Quantity(first), toBlock = "latest", address = AccountAddress.ToString() },
            "omitted-to" => new { fromBlock = VerifiedRpc.Quantity(first), address = AccountAddress.ToString() },
            _ => new { fromBlock = VerifiedRpc.Quantity(first), toBlock = VerifiedRpc.Quantity(last), address = AccountAddress.ToString() },
        };

        JsonElement logs = (JsonElement)await rpc.InvokeAsync("eth_getLogs", Parameters(filter), CancellationToken.None);

        AssertRangeLogs(logs, source, first, last);
        Assert.That(source.Anchors[0].BlockHash, Is.EqualTo(source.Head(last > 320 ? 325UL : 320UL).BlockHash));
    }

    [Test]
    public async Task Log_range_width_is_bounded([Values] bool optimistic, [Values(256, 257, 498, 511, 512)] int depth)
    {
        LogRangeSource source = new(143, 655);
        using VerifiedRpc rpc = new(source, () => source.Head(optimistic ? 645UL : 655UL), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(655));
        JsonElement parameters = Parameters(new
        {
            fromBlock = VerifiedRpc.Quantity(655UL - (ulong)depth),
            toBlock = optimistic ? "latest" : "finalized",
            address = AccountAddress.ToString(),
        });

        if (depth <= 511)
        {
            JsonElement logs = (JsonElement)await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
            AssertRangeLogs(logs, source, 655UL - (ulong)depth, 655);
        }
        else
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None));
            using IDisposable assertions = Assert.EnterMultipleScope();
            Assert.That(error!.Code, Is.EqualTo(-32001));
            Assert.That(source.HeaderReads, Is.Zero);
            Assert.That(source.Anchors, Is.Empty);
        }
    }

    [Test]
    public async Task Historical_log_block_hash_does_not_expand_block_data_history([Values(256, 257, 499)] int depth)
    {
        LogRangeSource source = new(100, 605);
        ulong number = 600UL - (ulong)depth;
        using VerifiedRpc rpc = new(source, () => source.Head(600), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(605));
        string hash = source[number].Hash!.ToString();

        JsonElement logs = (JsonElement)await rpc.InvokeAsync("eth_getLogs",
            Parameters(new { blockHash = hash, address = AccountAddress.ToString() }), CancellationToken.None);

        AssertRangeLogs(logs, source, number, number);
        if (depth > 256)
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
                await rpc.InvokeAsync("eth_getBlockByHash", Parameters(hash, false), CancellationToken.None));
            Assert.That(error!.Code, Is.EqualTo(-32001));
        }
    }

    [Test]
    public async Task Log_range_rejects_forged_headers_ancestry_finality_and_receipts(
        [Values("header-number", "header-hash", "parent", "finalized", "receipts", "ancestor-count", "ancestor-tail")] string forgery)
    {
        LogRangeSource source = new();
        VerifiedHead finalized = source.Head(320);
        if (forgery == "finalized") finalized = finalized with { BlockHash = Keccak.Compute("conflicting finality") };
        if (forgery == "receipts") source.TamperReceipts = true;
        source.TransformHeader = header =>
        {
            if (forgery == "parent" && header.Number == 311)
            {
                header = header.Clone();
                header.ParentHash = Keccak.Compute("forged parent");
            }
            if (header.Number == 310 && forgery is "header-number" or "header-hash")
            {
                header = header.Clone();
                if (forgery == "header-number") header.Number++;
                else header.Hash = Keccak.Compute("forged header hash");
            }
            return header;
        };
        source.TransformAncestors = hashes => forgery switch
        {
            "ancestor-count" => hashes[..^1],
            "ancestor-tail" => [.. hashes[..^1], Keccak.Compute("forged ancestor tail")],
            _ => hashes,
        };
        using VerifiedRpc rpc = new(source, () => finalized, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs",
            Parameters(new { fromBlock = VerifiedRpc.Quantity(310), toBlock = "latest" }), CancellationToken.None));

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(error!.Code, Is.EqualTo(-32000));
        Assert.That(source.ReceiptReads, Is.EqualTo(forgery == "receipts" ? 1 : 0));
    }

    [TestCase("0x191", "latest", -32602)]
    [TestCase("0x190", "0x191", -32602)]
    [TestCase("0x186", "0x185", -32602)]
    [TestCase("0x10000000000000000", "latest", -32602)]
    [TestCase("safe", "latest", -32001)]
    [TestCase("pending", "latest", -32001)]
    [TestCase("earliest", "latest", -32001)]
    [TestCase("0x0190", "latest", -32602)]
    public async Task Log_range_rejects_unavailable_or_invalid_bounds_before_network(string from, string to, int code)
    {
        LogRangeSource source = new(300, 400);
        using VerifiedRpc rpc = new(source, () => source.Head(390), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(400));

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs",
            Parameters(new { fromBlock = from, toBlock = to }), CancellationToken.None));

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(error!.Code, Is.EqualTo(code));
        Assert.That(source.HeaderReads, Is.Zero);
        Assert.That(source.Anchors, Is.Empty);
    }

    [Test]
    public async Task Reversed_recent_log_range_is_invalid_without_network([Values] bool omittedFrom)
    {
        LogRangeSource source = new();
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));

        object filter = omittedFrom ? new { toBlock = VerifiedRpc.Quantity(324) }
            : new { fromBlock = "latest", toBlock = VerifiedRpc.Quantity(324) };
        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs",
            Parameters(filter), CancellationToken.None));

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(error!.Code, Is.EqualTo(-32602));
        Assert.That(source.HeaderReads, Is.Zero);
        Assert.That(source.Anchors, Is.Empty);
    }

    [Test]
    public async Task Log_range_without_latest_distinguishes_unavailable_latest_from_a_numeric_future([Values] bool semanticLatest)
    {
        LogRangeSource source = new();
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => null);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs",
            Parameters(new { fromBlock = "finalized", toBlock = semanticLatest ? "latest" : VerifiedRpc.Quantity(321) }), CancellationToken.None));

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(error!.Code, Is.EqualTo(semanticLatest ? -32001 : -32602));
        Assert.That(source.HeaderReads, Is.Zero);
        Assert.That(source.Anchors, Is.Empty);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Log_range_pins_both_heads_during_a_concurrent_update(CancellationToken token)
    {
        LogRangeSource source = new(300, 327) { AncestorGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        VerifiedHead finalized = source.Head(320);
        VerifiedHead latest = source.Head(325);
        int finalizedReads = 0, latestReads = 0;
        using VerifiedRpc rpc = new(source, () => { finalizedReads++; return finalized; }, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => { latestReads++; return latest; });
        latestReads = 0;

        Task<object> request = rpc.InvokeAsync("eth_getLogs",
            Parameters(new { fromBlock = "finalized", toBlock = "latest", address = AccountAddress.ToString() }), token);
        try
        {
            Task first = await Task.WhenAny(request, source.AncestorsStarted.Task).WaitAsync(token);
            Assert.That(first, Is.SameAs(source.AncestorsStarted.Task), "the range must enter ancestry verification");
            finalized = source.Head(321);
            latest = source.Head(327);
            source.AncestorGate.SetResult();
            JsonElement logs = (JsonElement)await request;

            AssertRangeLogs(logs, source, 320, 325);
            using IDisposable assertions = Assert.EnterMultipleScope();
            Assert.That(finalizedReads, Is.EqualTo(1));
            Assert.That(latestReads, Is.EqualTo(1));
            Assert.That(source.Anchors.Single().BlockHash, Is.EqualTo(source.Head(325).BlockHash));
        }
        finally
        {
            source.AncestorGate.TrySetResult();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Log_range_cancellation_does_not_return_a_partial_result(CancellationToken token)
    {
        LogRangeSource source = new() { AncestorGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));

        Task<object> request = rpc.InvokeAsync("eth_getLogs",
            Parameters(new { fromBlock = "finalized", toBlock = "latest" }), caller.Token);
        try
        {
            Task first = await Task.WhenAny(request, source.AncestorsStarted.Task).WaitAsync(token);
            Assert.That(first, Is.SameAs(source.AncestorsStarted.Task), "the range must enter ancestry verification");
            caller.Cancel();

            Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(source.ReceiptReads, Is.Zero);
        }
        finally
        {
            caller.Cancel();
            source.AncestorGate.TrySetResult();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Finalized_log_results_reuse_equivalent_filters_across_finality_progress([Values] bool empty)
    {
        LogRangeSource source = new();
        VerifiedHead finalized = source.Head(320);
        string topic = Keccak.Compute("recent range log").ToString();
        string otherTopic = Keccak.Compute("another topic").ToString();
        string unusedAddress = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
        object?[] topics = empty ? [new[] { topic, otherTopic }, null] : [new[] { topic, otherTopic }];
        using VerifiedRpc rpc = new(source, () => finalized, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        JsonElement first = (JsonElement)await rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            fromBlock = "0x136", toBlock = "0x138", address = new[] { AccountAddress.ToString(), unusedAddress }, topics,
        }), CancellationToken.None);
        int headerReads = source.HeaderReads;
        finalized = source.Head(325);
        object?[] equivalentTopics = empty ? [new[] { otherTopic, topic, topic }, null] : [new[] { otherTopic, topic, topic }];

        JsonElement repeated = (JsonElement)await rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            fromBlock = "0x136", toBlock = "0x138",
            address = new[] { "0x" + unusedAddress[2..].ToUpperInvariant(), AccountAddress.ToString(), AccountAddress.ToString() },
            topics = equivalentTopics,
        }), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.GetArrayLength(), Is.EqualTo(empty ? 0 : 6));
            Assert.That(repeated.GetRawText(), Is.EqualTo(first.GetRawText()));
            Assert.That(source.BodyNumbers, Is.EqualTo(new ulong[] { 310, 311, 312 }));
            Assert.That(source.ReceiptReads, Is.EqualTo(3));
            Assert.That(source.HeaderReads, Is.GreaterThan(headerReads), "cache hits still authenticate current canonical ancestry");
        }
        if (!empty) AssertRangeLogs(repeated, source, 310, 312);
    }

    [Test]
    public async Task Finalized_log_cache_separates_filters_and_ranges(
        [Values("address", "topic", "arity", "first", "last")] string change)
    {
        LogRangeSource source = new();
        string topic = Keccak.Compute("recent range log").ToString();
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        JsonElement originalParameters = Parameters(new
        {
            fromBlock = "0x136", toBlock = "0x138", address = AccountAddress.ToString(), topics = new[] { topic },
        });
        JsonElement original = (JsonElement)await rpc.InvokeAsync("eth_getLogs", originalParameters, CancellationToken.None);
        object?[] topics = change == "arity" ? [topic, null] : [change == "topic" ? Keccak.Compute("absent").ToString() : topic];

        JsonElement different = (JsonElement)await rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            fromBlock = change == "first" ? "0x137" : "0x136",
            toBlock = change == "last" ? "0x137" : "0x138",
            address = change == "address" ? Address.Zero.ToString() : AccountAddress.ToString(), topics,
        }), CancellationToken.None);
        int reads = source.ReceiptReads;
        JsonElement repeated = (JsonElement)await rpc.InvokeAsync("eth_getLogs", originalParameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(different.GetRawText(), Is.Not.EqualTo(original.GetRawText()));
            Assert.That(different.GetArrayLength(), Is.EqualTo(change is "topic" or "arity" ? 0 : change is "first" or "last" ? 4 : 6));
            Assert.That(repeated.GetRawText(), Is.EqualTo(original.GetRawText()));
            Assert.That(source.ReceiptReads, Is.EqualTo(reads));
        }
    }

    [Test]
    public async Task Cached_log_results_still_reject_invalid_or_expired_ancestry(
        [Values("header", "finality", "history-window")] string change)
    {
        LogRangeSource source = new();
        VerifiedHead finalized = source.Head(320);
        using VerifiedRpc rpc = new(source, () => finalized, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        JsonElement parameters = Parameters(new { fromBlock = "0x136", toBlock = "0x138", address = AccountAddress.ToString() });
        await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
        if (change == "header")
            source.TransformHeader = header => { BlockHeader forged = header.Clone(); forged.Number++; return forged; };
        else if (change == "finality") finalized = finalized with { BlockHash = Keccak.Compute("conflicting finality") };
        else finalized = finalized with { Number = 310 + VerifiedLogHeaders.HistoryCapacity };

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.Code, Is.EqualTo(change == "history-window" ? -32001 : -32000));
            Assert.That(source.ReceiptReads, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task Log_result_cache_is_bound_to_finalized_canonical_block_hashes()
    {
        LogRangeSource source = new();
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        JsonElement parameters = Parameters(new { fromBlock = "0x136", toBlock = "0x138", address = AccountAddress.ToString() });
        JsonElement before = (JsonElement)await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
        source.ReorgFrom(310);

        JsonElement after = (JsonElement)await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);

        AssertRangeLogs(after, source, 310, 312);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.GetRawText(), Is.Not.EqualTo(before.GetRawText()));
            Assert.That(source.ReceiptReads, Is.EqualTo(6));
        }
    }

    [Test]
    public async Task Optimistic_log_ranges_are_cached_only_after_finalization()
    {
        LogRangeSource source = new();
        VerifiedHead finalized = source.Head(309);
        using VerifiedRpc rpc = new(source, () => finalized, 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        JsonElement parameters = Parameters(new { fromBlock = "0x136", toBlock = "0x138", address = AccountAddress.ToString() });
        for (int repeat = 0; repeat < 2; repeat++) await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
        Assert.That(source.ReceiptReads, Is.EqualTo(6));
        finalized = source.Head(320);
        for (int repeat = 0; repeat < 2; repeat++) await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
        Assert.That(source.ReceiptReads, Is.EqualTo(9));
    }

    [Test]
    public async Task Failed_or_canceled_log_queries_do_not_cache_partial_results([Values] bool cancel)
    {
        using CancellationTokenSource caller = new();
        bool fail = true;
        LogRangeSource source = new()
        {
            BeforeReceiptRead = (number, token) =>
            {
                if (fail && number == 311)
                {
                    if (cancel) { caller.Cancel(); token.ThrowIfCancellationRequested(); }
                    throw new InvalidDataException("Invalid receipts after one verified block.");
                }
                return Task.CompletedTask;
            },
        };
        using VerifiedRpc rpc = new(source, () => source.Head(320), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(325));
        JsonElement parameters = Parameters(new { fromBlock = "0x136", toBlock = "0x138", address = AccountAddress.ToString() });
        if (cancel)
            Assert.That(async () => await rpc.InvokeAsync("eth_getLogs", parameters, caller.Token), Throws.InstanceOf<OperationCanceledException>());
        else
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_getLogs", parameters, caller.Token));
            Assert.That(error!.Code, Is.EqualTo(-32000));
        }
        fail = false;

        JsonElement recovered = (JsonElement)await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
        int reads = source.ReceiptReads;
        JsonElement repeated = (JsonElement)await rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);

        AssertRangeLogs(recovered, source, 310, 312);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repeated.GetRawText(), Is.EqualTo(recovered.GetRawText()));
            Assert.That(source.ReceiptReads, Is.EqualTo(reads));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Log_prefetch_refills_before_a_later_candidate_finishes(CancellationToken token)
    {
        Channel<ulong> started = Channel.CreateUnbounded<ulong>();
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource straggler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogRangeSource source = new(300, 304)
        {
            BeforeReceiptRead = async (number, requestToken) =>
            {
                started.Writer.TryWrite(number);
                if (number == 300) await first.Task.WaitAsync(requestToken);
                if (number == 303) await straggler.Task.WaitAsync(requestToken);
            },
        };
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using VerifiedRpc rpc = new(source, () => source.Head(304), 1, specProvider: MainnetSpecProvider.Instance);
        Task<object> request = rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            fromBlock = VerifiedRpc.Quantity(300), toBlock = "finalized", address = AccountAddress.ToString(),
        }), caller.Token);
        try
        {
            for (ulong number = 300; number < 304; number++)
                Assert.That(await started.Reader.ReadAsync(token), Is.EqualTo(number));
            first.SetResult();
            Assert.That(await started.Reader.ReadAsync(token), Is.EqualTo(304), "the fifth candidate must start while the fourth remains pending");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(straggler.Task.IsCompleted, Is.False);
                Assert.That(request.IsCompleted, Is.False);
            }
            straggler.SetResult();
            AssertRangeLogs((JsonElement)await request.WaitAsync(token), source, 300, 304);
        }
        finally
        {
            await caller.CancelAsync();
            first.TrySetResult();
            straggler.TrySetResult();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Parallel_log_prefetch_keeps_canonical_coordinates_and_four_payload_limit(
        [Values] bool sparse, CancellationToken token)
    {
        ulong last = sparse ? 315UL : 307UL;
        bool Includes(ulong number) => !sparse || number % 2 == 0;
        ulong[] candidates = Enumerable.Range(300, checked((int)(last - 299))).Select(number => (ulong)number).Where(Includes).ToArray();
        Dictionary<ulong, TaskCompletionSource> gates = Enumerable.Range(300, checked((int)(last - 299)))
            .ToDictionary(number => (ulong)number, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Channel<(ulong Number, int Active)> started = Channel.CreateUnbounded<(ulong, int)>();
        ConcurrentQueue<int> concurrency = new();
        int active = 0;
        LogRangeSource source = new(300, last, includeLogs: Includes)
        {
            BeforeReceiptRead = async (number, requestToken) =>
            {
                int pending = Interlocked.Increment(ref active);
                concurrency.Enqueue(pending);
                started.Writer.TryWrite((number, pending));
                try { await gates[number].Task.WaitAsync(requestToken); }
                finally { Interlocked.Decrement(ref active); }
            },
        };
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using VerifiedRpc rpc = new(source, () => source.Head(last), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(last));
        Task<object> request = rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            fromBlock = VerifiedRpc.Quantity(300),
            toBlock = "finalized",
            address = AccountAddress.ToString(),
        }), caller.Token);
        try
        {
            for (int offset = 0; offset < candidates.Length; offset += 4)
            {
                List<ulong> batch = [];
                for (int i = 0; i < 4; i++)
                {
                    (ulong number, int pending) = await started.Reader.ReadAsync(token);
                    batch.Add(number);
                    Assert.That(pending, Is.InRange(1, 4));
                }
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(batch, Is.EqualTo(candidates.Skip(offset).Take(4)));
                    Assert.That(source.BodyNumbers.Count, Is.EqualTo(offset + 4), "prefetch must not issue a fifth payload while four are pending");
                    Assert.That(request.IsCompleted, Is.False);
                }
                for (int i = batch.Count - 1; i >= 0; i--)
                {
                    gates[batch[i]].SetResult();
                    Assert.That(await source.ReceiptsCompleted.Reader.ReadAsync(token), Is.EqualTo(batch[i]));
                }
            }
            JsonElement logs = (JsonElement)await request.WaitAsync(token);
            AssertRangeLogs(logs, source, 300, last, Includes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.BodyNumbers, Is.EqualTo(candidates), "bloom-negative headers must never request block bodies");
                Assert.That(source.ReceiptReads, Is.EqualTo(candidates.Length));
                Assert.That(concurrency.Max(), Is.EqualTo(4));
                Assert.That(active, Is.Zero);
            }
        }
        finally
        {
            await caller.CancelAsync();
            foreach (TaskCompletionSource gate in gates.Values) gate.TrySetResult();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Failed_or_canceled_log_prefetch_drains_siblings_without_partial_success(
        [Values] bool receipts, [Values] bool cancel, CancellationToken token)
    {
        Channel<ulong> started = Channel.CreateUnbounded<ulong>();
        TaskCompletionSource fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int canceling = 0;
        int drained = 0;
        async Task WaitForPayloadAsync(ulong number, CancellationToken requestToken)
        {
            started.Writer.TryWrite(number);
            if (number == 300) return;
            try
            {
                if (number == 302)
                {
                    await fail.Task.WaitAsync(requestToken);
                    throw new InvalidDataException("Invalid payload from a controlled peer.");
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, requestToken);
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested && number is 301 or 303)
            {
                if (Interlocked.Increment(ref canceling) == 2) canceled.TrySetResult();
                await release.Task;
                Interlocked.Increment(ref drained);
                throw;
            }
        }
        LogRangeSource source = new(300, 307)
        {
            BeforeBlockRead = receipts ? null : WaitForPayloadAsync,
            BeforeReceiptRead = receipts ? WaitForPayloadAsync : null,
        };
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        using VerifiedRpc rpc = new(source, () => source.Head(307), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Head(307));
        Task<object> request = rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            fromBlock = VerifiedRpc.Quantity(300),
            toBlock = "finalized",
            address = AccountAddress.ToString(),
        }), caller.Token);
        try
        {
            for (ulong number = 300; number < 305; number++)
                Assert.That(await started.Reader.ReadAsync(token), Is.EqualTo(number));
            Assert.That(await source.ReceiptsCompleted.Reader.ReadAsync(token), Is.EqualTo(300), "one verified block is available before the later failure");
            if (cancel) await caller.CancelAsync();
            else fail.SetResult();
            await canceled.Task.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(request.IsCompleted, Is.False, "the RPC must wait for both sibling requests to drain");
                Assert.That(drained, Is.Zero);
                Assert.That(source.BodyNumbers.Count, Is.EqualTo(5));
            }
            release.SetResult();
            if (cancel)
                Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            else
            {
                RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await request);
                Assert.That(error!.Code, Is.EqualTo(-32000));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(drained, Is.EqualTo(2));
                Assert.That(source.BodyNumbers.Count, Is.EqualTo(5), "failed prefetch must not refill the remaining window");
            }
        }
        finally
        {
            await caller.CancelAsync();
            release.TrySetResult();
            fail.TrySetResult();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static void AssertRangeLogs(JsonElement logs, LogRangeSource source, ulong first, ulong last,
        Func<ulong, bool>? includeBlock = null)
    {
        List<(string Number, string Hash, string Transaction, string TransactionIndex, string LogIndex, string Data)> expected = [];
        for (ulong number = first; number <= last; number++)
        {
            if (includeBlock is not null && !includeBlock(number)) continue;
            for (int transaction = 0; transaction < 2; transaction++)
                expected.Add((VerifiedRpc.Quantity(number), source[number].Hash!.ToString(), source[number].Transactions[transaction].Hash!.ToString(),
                    VerifiedRpc.Quantity((ulong)transaction), VerifiedRpc.Quantity((ulong)(2 * transaction + 1)), $"0x{number % 256:x2}{transaction:x2}"));
        }
        Assert.That(logs.EnumerateArray().Select(log => (
            log.GetProperty("blockNumber").GetString(), log.GetProperty("blockHash").GetString(), log.GetProperty("transactionHash").GetString(),
            log.GetProperty("transactionIndex").GetString(), log.GetProperty("logIndex").GetString(), log.GetProperty("data").GetString())), Is.EqualTo(expected));
    }

    [Test]
    public async Task Block_receipts_logs_and_transaction_index_use_verified_complete_lists()
    {
        using Provider provider = new(new Account(7, 123));

        JsonElement block = (JsonElement)await provider.Rpc.InvokeAsync("eth_getBlockByNumber", Parameters("finalized", false), CancellationToken.None);
        JsonElement receipts = (JsonElement)await provider.Rpc.InvokeAsync("eth_getBlockReceipts", Parameters("finalized"), CancellationToken.None);
        JsonElement logs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", Parameters(new { blockHash = BlockHash.ToString() }), CancellationToken.None);
        JsonElement tx = (JsonElement)await provider.Rpc.InvokeAsync("eth_getTransactionByBlockNumberAndIndex", Parameters("finalized", "0x0"), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(BlockHash.ToString()));
            Assert.That(block.GetProperty("transactions").GetArrayLength(), Is.Zero);
            Assert.That(receipts.GetArrayLength(), Is.Zero);
            Assert.That(logs.GetArrayLength(), Is.Zero);
            Assert.That(tx.ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
    }

    [Test]
    public async Task Transaction_receipt_and_logs_are_derived_from_verified_roots([Values(-1, 0, 1)] int headerGasOffset)
    {
        using Provider provider = new(new Account(7, 123));
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction transaction = new()
        {
            Nonce = 2,
            GasLimit = 50_000,
            GasPrice = 2,
            To = AccountAddress,
            Value = 3,
        };
        new EthereumEcdsa(1).Sign(key, transaction, isEip155Enabled: true);
        transaction.Hash = transaction.CalculateHash();
        Hash256 topic = Keccak.Compute("verified topic");
        TxReceipt receipt = new()
        {
            TxType = transaction.Type,
            StatusCode = 1,
            GasUsedTotal = 21_375,
            Logs = [new LogEntry(AccountAddress, [0xab, 0xcd], [topic])],
        };
        provider.Transactions = [transaction];
        provider.Receipts = [receipt];
        provider.Header.TxRoot = TxTrie.CalculateRoot(provider.Transactions);
        provider.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(provider.Header),
            provider.Receipts, new ReceiptMessageDecoder());
        provider.Header.GasUsed = (ulong)((long)receipt.GasUsedTotal + headerGasOffset);
        provider.Header.Bloom = new Bloom(receipt.Logs);

        JsonElement tx = (JsonElement)await provider.Rpc.InvokeAsync("eth_getTransactionByBlockHashAndIndex",
            Parameters(BlockHash.ToString(), "0x0"), CancellationToken.None);
        JsonElement rpcReceipt = (JsonElement)await provider.Rpc.InvokeAsync("eth_getBlockReceipts", Parameters("finalized"), CancellationToken.None);
        JsonElement logs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            blockHash = BlockHash.ToString(),
            address = AccountAddress.ToString(),
            topics = new[] { topic.ToString() }
        }), CancellationToken.None);
        JsonElement emptyFilterLogs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            blockHash = BlockHash.ToString(),
            address = Array.Empty<string>(),
            topics = new[] { Array.Empty<string>() }
        }), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.GetProperty("hash").GetString(), Is.EqualTo(transaction.Hash!.ToString()));
            Assert.That(tx.GetProperty("from").GetString(), Is.EqualTo(key.Address.ToString()));
            Assert.That(rpcReceipt[0].GetProperty("transactionHash").GetString(), Is.EqualTo(transaction.Hash.ToString()));
            Assert.That(rpcReceipt[0].GetProperty("gasUsed").GetString(), Is.EqualTo("0x537f"));
            Assert.That(logs.GetArrayLength(), Is.EqualTo(1));
            Assert.That(logs[0].GetProperty("data").GetString(), Is.EqualTo("0xabcd"));
            Assert.That(emptyFilterLogs.GetArrayLength(), Is.EqualTo(1), "empty address and topic lists are wildcards");
        }

        provider.Header.ReceiptsRoot = Keccak.Compute("forged receipts");
        await AssertRpcErrorAsync(provider, "eth_getBlockReceipts", Parameters("finalized"), -32000);
    }

    [Test]
    public async Task Filtered_logs_keep_block_coordinates_without_recovering_senders([Values] bool withoutBloom)
    {
        Hash256 topic = Keccak.Compute("matching log topic");
        Hash256 otherTopic = Keccak.Compute("other log topic");
        using Provider provider = LogsProvider(
            [new LogEntry(AccountAddress, [], [otherTopic]), new LogEntry(Address.Zero, [], [topic])],
            [new LogEntry(AccountAddress, [], []), new LogEntry(AccountAddress, [0xab], [otherTopic, topic])]);
        if (withoutBloom) provider.Header.Bloom = Bloom.ZeroLength;

        JsonElement logs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            blockHash = BlockHash.ToString(),
            address = AccountAddress.ToString(),
            topics = new object?[] { null, topic.ToString() },
        }), CancellationToken.None);

        Assert.That(logs.GetArrayLength(), Is.EqualTo(1));
        JsonElement log = logs[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.GetProperty("logIndex").GetString(), Is.EqualTo("0x3"));
            Assert.That(log.GetProperty("transactionIndex").GetString(), Is.EqualTo("0x1"));
            Assert.That(log.GetProperty("transactionHash").GetString(), Is.EqualTo(provider.Transactions[1].Hash!.ToString()));
            Assert.That(log.GetProperty("blockHash").GetString(), Is.EqualTo(BlockHash.ToString()));
            Assert.That(log.GetProperty("blockNumber").GetString(), Is.EqualTo("0xa"));
            Assert.That(log.GetProperty("blockTimestamp").GetString(), Is.EqualTo("0x6553f100"));
            Assert.That(log.GetProperty("removed").GetBoolean(), Is.False);
            Assert.That(log.GetProperty("address").GetString(), Is.EqualTo(AccountAddress.ToString()));
            Assert.That(log.GetProperty("data").GetString(), Is.EqualTo("0xab"));
            Assert.That(log.GetProperty("topics")[1].GetString(), Is.EqualTo(topic.ToString()));
            Assert.That(provider.Transactions.All(transaction => transaction.SenderAddress is null), Is.True);
            Assert.That(provider.BlockFetches, Is.EqualTo(1));
            Assert.That(provider.ReceiptFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Negative_authenticated_bloom_skips_execution_data([Values] bool filterTopic)
    {
        using Provider provider = LogsProvider([new LogEntry(AccountAddress, [], [Keccak.Compute("present topic")])]);
        object filter = filterTopic
            ? new { blockHash = BlockHash.ToString(), topics = new[] { Keccak.Compute("absent topic").ToString() } }
            : new { blockHash = BlockHash.ToString(), address = Address.Zero.ToString() };

        JsonElement logs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", Parameters(filter), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs.GetArrayLength(), Is.Zero);
            Assert.That(provider.BlockFetches, Is.Zero);
            Assert.That(provider.ReceiptFetches, Is.Zero);
        }
    }

    [Test]
    public async Task Bloom_candidates_still_verify_and_filter_receipts([Values] bool tamper)
    {
        Hash256 topic = Keccak.Compute("candidate topic");
        using Provider provider = LogsProvider(
            [new LogEntry(AccountAddress, [], [Keccak.Compute("other topic")]), new LogEntry(Address.Zero, [], [topic])]);
        if (tamper) provider.Header.ReceiptsRoot = Keccak.Compute("forged receipts");
        JsonElement parameters = Parameters(new
        {
            blockHash = BlockHash.ToString(),
            address = AccountAddress.ToString(),
            topics = new[] { topic.ToString() },
        });

        if (tamper)
            await AssertRpcErrorAsync(provider, "eth_getLogs", parameters, -32000);
        else
        {
            JsonElement logs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", parameters, CancellationToken.None);
            Assert.That(logs.GetArrayLength(), Is.Zero, "bloom membership does not establish a matching log");
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.BlockFetches, Is.EqualTo(1));
            Assert.That(provider.ReceiptFetches, Is.EqualTo(1));
            Assert.That(provider.Transactions[0].SenderAddress, Is.Null);
        }
    }

    private static Provider LogsProvider(params LogEntry[][] logs)
    {
        Provider provider = new(new Account(7, 123));
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        provider.Transactions = new Transaction[logs.Length];
        provider.Receipts = new TxReceipt[logs.Length];
        for (int i = 0; i < logs.Length; i++)
        {
            Transaction transaction = new() { Nonce = (ulong)i, GasLimit = 50_000, GasPrice = 2, To = AccountAddress };
            new EthereumEcdsa(1).Sign(key, transaction, isEip155Enabled: true);
            transaction.Hash = transaction.CalculateHash();
            transaction.SenderAddress = null;
            provider.Transactions[i] = transaction;
            provider.Receipts[i] = new TxReceipt
            {
                TxType = transaction.Type,
                StatusCode = 1,
                GasUsedTotal = (ulong)((i + 1) * 21_000),
                Logs = logs[i],
            };
        }
        provider.Header.TxRoot = TxTrie.CalculateRoot(provider.Transactions);
        provider.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(provider.Header),
            provider.Receipts, new ReceiptMessageDecoder());
        provider.Header.GasUsed = (ulong)(logs.Length * 21_000);
        provider.Header.Bloom = new Bloom(logs.SelectMany(entries => entries).ToArray());
        return provider;
    }

    [Test]
    public async Task Invalid_log_filters_reject_before_block_fetch()
    {
        using Provider provider = new(new Account(7, 123));

        await AssertRpcErrorAsync(provider, "eth_getLogs", Parameters(new { blockHash = BlockHash.ToString(), fromBlock = "finalized" }), -32602);
        await AssertRpcErrorAsync(provider, "eth_getLogs", Parameters(new { topics = new string[5] }), -32602);
        await AssertRpcErrorAsync(provider, "eth_getLogs", Parameters(new { address = "0x12" }), -32602);
        Assert.That(provider.BlockFetches, Is.Zero);
    }

    [Test]
    public async Task Authenticated_logs_are_bounded_across_blocks_before_hex_rendering()
    {
        using Provider provider = new(new Account(7, 123));
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction transaction = new() { GasLimit = 60_000_000, GasPrice = 2, To = AccountAddress };
        new EthereumEcdsa(1).Sign(key, transaction, isEip155Enabled: true);
        transaction.Hash = transaction.CalculateHash();
        byte[] data = new byte[1024 * 1024];
        provider.Transactions = [transaction];
        provider.Receipts = [new TxReceipt
        {
            TxType = transaction.Type, StatusCode = 1, GasUsedTotal = 30_000_000,
            Logs = [new LogEntry(AccountAddress, data, []), new LogEntry(AccountAddress, data, []), new LogEntry(AccountAddress, data, [])],
        }];
        provider.Header.GasLimit = 60_000_000;
        provider.Header.GasUsed = 30_000_000;
        provider.Header.TxRoot = TxTrie.CalculateRoot(provider.Transactions);
        provider.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(provider.Header), provider.Receipts, new ReceiptMessageDecoder());
        BlockHeader older = Provider.CreateHeader(provider.Header.Number - 1, Keccak.Compute("older authenticated logs"), provider.Head.StateRoot);
        older.TxRoot = provider.Header.TxRoot;
        older.ReceiptsRoot = provider.Header.ReceiptsRoot;
        older.GasLimit = provider.Header.GasLimit;
        older.GasUsed = provider.Header.GasUsed;
        using VerifiedExecutionData execution = new(provider, MainnetSpecProvider.Instance, 1);

        JsonElement one = await execution.GetLogsAsync([provider.Header], null, [], CancellationToken.None);
        Assert.That(one.GetArrayLength(), Is.EqualTo(3));
        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
            await execution.GetLogsAsync([older, provider.Header], null, [], CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo(-32005));
    }

    [Test]
    [NonParallelizable]
    public async Task Execution_data_admission_is_prompt_and_released_after_failure([Values] bool cancel)
    {
        using Provider provider = new(new Account(7, 123)) { BlockGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using CancellationTokenSource cancellation = new();
        Task<object>[] active = Enumerable.Range(0, 4).Select(_ => provider.Rpc.InvokeAsync("eth_getBlockByNumber",
            Parameters("finalized", false), cancellation.Token)).ToArray();
        try
        {
            Assert.That(provider.BlockFetches, Is.EqualTo(4));
            RpcException? busy = await Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.InvokeAsync("eth_getBlockByNumber",
                Parameters("finalized", false), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.That(busy!.Code, Is.EqualTo(-32005));
            if (cancel) cancellation.Cancel();
            else provider.BlockGate!.SetException(new InvalidDataException("Invalid authenticated peer body."));
            foreach (Task<object> task in active)
            {
                if (cancel) await Assert.CatchAsync<OperationCanceledException>(async () => await task);
                else Assert.That((await Assert.ThrowsAsync<RpcException>(async () => await task))!.Code, Is.EqualTo(-32000));
            }
            provider.BlockGate = null;
            JsonElement next = (JsonElement)await provider.Rpc.InvokeAsync("eth_getBlockByNumber", Parameters("finalized", false), CancellationToken.None);
            Assert.That(next.GetProperty("transactions").GetArrayLength(), Is.Zero);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(active.Select(async task => { try { await task; } catch (Exception) { } }));
        }
    }

    [Test]
    public async Task Current_number_and_hash_selectors_are_accepted([Values(0, 1, 2, 3)] int selectorKind, [Values] bool staleLatest)
    {
        object selector = selectorKind switch
        {
            0 => "0xa",
            1 => new { blockNumber = "0xa" },
            2 => new { blockHash = BlockHash.ToString(), requireCanonical = true },
            _ => BlockHash.ToString()
        };
        using Provider provider = new(new Account(7, 123)) { CurrentSlot = staleLatest ? 111UL : null };

        Assert.That(await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), selector), CancellationToken.None), Is.EqualTo("0x7b"));
    }

    [Test]
    public async Task Historical_state_selectors_stop_at_32_finalized_ancestors(
        [Values(0, 1, 2, 3)] int selectorKind, [Values(32, 33)] int depth)
    {
        using Provider provider = new(new Account(7, 123), headNumber: 300);
        ulong number = provider.Head.Number - (ulong)depth;
        provider.HistoricalHeader = Provider.CreateHeader(number, Keccak.Compute("historical block"), provider.Head.StateRoot);
        object selector = selectorKind switch
        {
            0 => $"0x{number:x}",
            1 => new { blockNumber = $"0x{number:x}" },
            2 => provider.HistoricalHeader.Hash!.ToString(),
            _ => new { blockHash = provider.HistoricalHeader.Hash!.ToString(), requireCanonical = true }
        };

        if (depth == 32)
            Assert.That(await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), selector), CancellationToken.None), Is.EqualTo("0x7b"));
        else
            await AssertRpcErrorAsync(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), selector), -32001);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Requests, Has.Count.EqualTo(depth == 32 ? 1 : 0));
            Assert.That(provider.CanonicalHeaderFetches, Is.EqualTo(depth == 32 ? 1 : 0));
        }
    }

    [Test]
    public async Task Pinned_optimistic_reads_survive_native_head_advancement_and_reject_reorgs(
        [Values("eth_getBalance", "eth_getCode", "eth_getProof")] string method, [Values(0, 1, 2, 3)] int selectorKind)
    {
        using Provider provider = OptimisticProvider();
        object selector = selectorKind switch
        {
            0 => $"0x{provider.HistoricalHeader!.Number:x}",
            1 => new { blockNumber = $"0x{provider.HistoricalHeader!.Number:x}" },
            2 => provider.HistoricalHeader!.Hash!.ToString(),
            _ => new { blockHash = provider.HistoricalHeader!.Hash!.ToString(), requireCanonical = true },
        };
        JsonElement ParametersFor(object selected) => method == "eth_getProof"
            ? Parameters(AccountAddress.ToString(), Array.Empty<string>(), selected) : Parameters(AccountAddress.ToString(), selected);

        object result = await provider.Rpc.InvokeAsync(method, ParametersFor(selector), CancellationToken.None);
        if (method == "eth_getProof")
        {
            JsonElement proof = (JsonElement)result;
            byte[][] witness = proof.GetProperty("accountProof").EnumerateArray().Select(item => Convert.FromHexString(item.GetString()!.AsSpan(2))).ToArray();
            Assert.That(ExecutionProofVerifier.VerifyAccount(provider.HistoricalHeader!.StateRoot!, AccountAddress, witness).Balance, Is.EqualTo((UInt256)123));
        }
        else Assert.That(result, Is.EqualTo(method == "eth_getBalance" ? "0x7b" : "0x602a00"));
        Assert.That(provider.LastCanonicalAnchor, Is.EqualTo(provider.LatestHead));
        Assert.That(provider.LastQueriedHead!.BlockHash, Is.EqualTo(provider.HistoricalHeader!.Hash));

        provider.ReorgOptimisticAncestor();
        await AssertRpcErrorAsync(provider, method, ParametersFor(new { blockHash = provider.HistoricalHeader.Hash!.ToString(), requireCanonical = true }), -32001);
        Assert.That(await provider.Rpc.InvokeAsync(method, ParametersFor("latest"), CancellationToken.None), Is.Not.Null,
            "Rejecting an orphan must release the historical request admission slot.");
    }

    [Test]
    public async Task Pinned_optimistic_block_is_anchored_to_native_latest([Values] bool byHash)
    {
        using Provider provider = OptimisticProvider();
        Hash256 hash = provider.HistoricalHeader!.Hash!;
        JsonElement block = (JsonElement)await provider.Rpc.InvokeAsync(byHash ? "eth_getBlockByHash" : "eth_getBlockByNumber",
            Parameters(byHash ? hash.ToString() : $"0x{provider.HistoricalHeader.Number:x}", false), CancellationToken.None);
        Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(hash.ToString()));
        Assert.That(provider.LastCanonicalAnchor, Is.EqualTo(provider.LatestHead));

        provider.ReorgOptimisticAncestor();
        await AssertRpcErrorAsync(provider, "eth_getBlockByHash", Parameters(hash.ToString(), false), -32001);
    }

    [Test]
    public async Task Optimistic_state_history_retains_the_32_ancestor_bound([Values(32, 33)] int depth)
    {
        using Provider provider = OptimisticProvider();
        provider.HistoricalHeader = Provider.CreateHeader(provider.LatestHead!.Number - (ulong)depth, Keccak.Compute("bounded optimistic history"), provider.Head.StateRoot);
        if (depth == 32)
            Assert.That(await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), $"0x{provider.HistoricalHeader.Number:x}"), CancellationToken.None), Is.EqualTo("0x7b"));
        else await AssertRpcErrorAsync(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), $"0x{provider.HistoricalHeader.Number:x}"), -32001);
        Assert.That(provider.CanonicalHeaderFetches, Is.EqualTo(depth == 32 ? 1 : 0));
    }

    private static Provider OptimisticProvider()
    {
        byte[] code = [0x60, 0x2a, 0x00];
        Provider provider = new(new Account(7, 123, Keccak.EmptyTreeHash, Keccak.Compute(code)), code: code, headNumber: 236);
        provider.HistoricalHeader = Provider.CreateHeader(300, Keccak.Zero, provider.Head.StateRoot);
        provider.HistoricalHeader.Hash = Keccak.Compute(Rlp.Encode(provider.HistoricalHeader).Bytes);
        provider.OptimisticHeader = Provider.CreateHeader(301, Keccak.Zero, provider.Head.StateRoot);
        provider.OptimisticHeader.ParentHash = provider.HistoricalHeader.Hash;
        provider.OptimisticHeader.Hash = Keccak.Compute(Rlp.Encode(provider.OptimisticHeader).Bytes);
        provider.LatestHead = new(101, 301, provider.OptimisticHeader.Hash, provider.Head.StateRoot);
        return provider;
    }

    [Test]
    public async Task Historical_reads_leave_capacity_for_latest_state(
        [Values(false, true)] bool holdHeader, [Values(false, true)] bool byHash)
    {
        using Provider provider = new(new Account(7, 123), headNumber: 300)
        {
            HistoricalGate = holdHeader ? null : new(TaskCreationOptions.RunContinuationsAsynchronously),
            HistoricalHeaderGate = holdHeader ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null,
        };
        provider.HistoricalHeader = Provider.CreateHeader(299, Keccak.Compute("historical block"), provider.Head.StateRoot);
        JsonElement historical = Parameters(AccountAddress.ToString(),
            byHash ? provider.HistoricalHeader.Hash!.ToString() : "0x12b");
        Task<object>[] requests = Enumerable.Range(0, 8)
            .Select(_ => provider.Rpc.InvokeAsync("eth_getBalance", historical, CancellationToken.None)).ToArray();

        try
        {
            int admitted = holdHeader
                ? (byHash ? provider.HeaderByHashFetches : provider.CanonicalHeaderFetches)
                : provider.HistoricalReads;
            Assert.That(admitted, Is.EqualTo(2));
            Assert.That(requests.Skip(2).All(static request => request.IsCompleted), Is.True);
            foreach (Task<object> rejected in requests.Skip(2))
            {
                RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rejected);
                Assert.That(error!.Code, Is.EqualTo(-32000));
            }
            object latest = await provider.Rpc.InvokeAsync("eth_getBalance",
                Parameters(AccountAddress.ToString(), "latest"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(latest, Is.EqualTo("0x7b"));
        }
        finally
        {
            provider.HistoricalGate?.TrySetResult();
            provider.HistoricalHeaderGate?.TrySetResult();
            try { await Task.WhenAll(requests); }
            catch (RpcException) { }
        }
    }

    [Test]
    public async Task Cancelled_historical_header_lookup_releases_capacity([Values(false, true)] bool byHash)
    {
        using Provider provider = new(new Account(7, 123), headNumber: 300)
        {
            HistoricalGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
            HistoricalHeaderGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        provider.HistoricalHeader = Provider.CreateHeader(299, Keccak.Compute("historical block"), provider.Head.StateRoot);
        JsonElement parameters = Parameters(AccountAddress.ToString(),
            byHash ? provider.HistoricalHeader.Hash!.ToString() : "0x12b");
        using CancellationTokenSource cancellation = new();

        Task<object> interrupted = provider.Rpc.InvokeAsync("eth_getBalance", parameters, cancellation.Token);
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await interrupted);
        provider.HistoricalHeaderGate.SetResult();

        Task<object>[] next = [provider.Rpc.InvokeAsync("eth_getBalance", parameters, CancellationToken.None),
            provider.Rpc.InvokeAsync("eth_getBalance", parameters, CancellationToken.None)];
        try { Assert.That(provider.HistoricalReads, Is.EqualTo(2)); }
        finally
        {
            provider.HistoricalGate.SetResult();
            await Task.WhenAll(next);
        }
    }

    [Test]
    public async Task Block_history_remains_available_at_256_ancestors([Values(false, true)] bool byHash)
    {
        using Provider provider = new(new Account(7, 123), headNumber: 300);
        provider.HistoricalHeader = Provider.CreateHeader(44, Keccak.Compute("historical block"), provider.Head.StateRoot);

        JsonElement block = (JsonElement)await provider.Rpc.InvokeAsync(byHash ? "eth_getBlockByHash" : "eth_getBlockByNumber",
            byHash ? Parameters(provider.HistoricalHeader.Hash!.ToString(), false) : Parameters("0x2c", false), CancellationToken.None);

        Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(provider.HistoricalHeader.Hash!.ToString()));
        Assert.That(provider.BlockFetches, Is.EqualTo(1));
    }

    [TestCase("eth_call", "[]", -32602)]
    [TestCase("eth_getBalance", "[]", -32602)]
    [TestCase("eth_getBalance", "[\"0x12\",\"finalized\"]", -32602)]
    [TestCase("eth_getStorageAt", "[\"0x1234567890123456789012345678901234567890\",\"0x10000000000000000000000000000000000000000000000000000000000000000\",\"finalized\"]", -32602)]
    [TestCase("eth_getBalance", "[\"0x1234567890123456789012345678901234567890\",{\"blockNumber\":\"0xa\",\"blockHash\":\"0x00\"}]", -32602)]
    public async Task Invalid_requests_do_not_reach_provider(string method, string json, int error)
    {
        using Provider provider = new(new Account(7, 123));
        using JsonDocument parameters = JsonDocument.Parse(json);

        await AssertRpcErrorAsync(provider, method, parameters.RootElement, error);
        Assert.That(provider.Requests, Is.Empty);
    }

    [TestCase("eth_chainId", "0x1")]
    [TestCase("eth_blockNumber", "0xa")]
    public async Task Local_metadata_does_not_reach_provider(string method, string expected)
    {
        using Provider provider = new(new Account(7, 123));

        object actual = await provider.Rpc.InvokeAsync(method, Parameters(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(provider.Requests, Is.Empty);
        }
    }

    [Test]
    public async Task Snapshot_is_captured_once_before_fetch()
    {
        using Provider provider = new(new Account(7, 123));
        int reads = 0;
        using VerifiedRpc rpc = new(provider, () => ++reads == 1 ? provider.Head : throw new InvalidOperationException("Snapshot read twice."), 1);

        Assert.That(await rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), CancellationToken.None), Is.EqualTo("0x7b"));
        Assert.That(reads, Is.EqualTo(1));
    }

    [Test]
    public async Task Cancellation_reaches_execution_fetch()
    {
        using Provider provider = new(new Account(7, 123)) { Cancel = true };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(async () => await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), cancellation.Token));
    }

    private static async Task AssertRpcErrorAsync(Provider provider, string method, JsonElement parameters, int code)
    {
        RpcException? exception = await Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.InvokeAsync(method, parameters, CancellationToken.None));
        Assert.That(exception!.Code, Is.EqualTo(code));
    }

    private static void AssertFinalizedRequest((string Kind, Hash256? BlockHash) request, string kind)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Kind, Is.EqualTo(kind));
            Assert.That(request.BlockHash, Is.EqualTo(BlockHash));
        }
    }

    private static JsonElement Parameters(params object[] values) => JsonSerializer.SerializeToElement(values);

    [Test]
    public async Task Exported_proof_can_be_verified_against_the_authenticated_root([Values] bool tamper)
    {
        UInt256 key = 1;
        byte[] leaf = Leaf(Keccak.Compute(key.ToBigEndian()), Rlp.Encode((UInt256)42).Bytes);
        using Provider provider = new(new Account(7, 123, Keccak.Compute(leaf), Keccak.OfAnEmptyString), leaf) { TamperProof = tamper };
        JsonElement parameters = Parameters(AccountAddress.ToString(), new[] { "0x01" }, "finalized");
        if (tamper) { await AssertRpcErrorAsync(provider, "eth_getProof", parameters, -32000); return; }
        JsonElement result = (JsonElement)await provider.Rpc.InvokeAsync("eth_getProof", parameters, CancellationToken.None);
        byte[][] accountProof = result.GetProperty("accountProof").EnumerateArray()
            .Select(static item => Convert.FromHexString(item.GetString()!.AsSpan(2))).ToArray();
        byte[][] slotProof = result.GetProperty("storageProof")[0].GetProperty("proof").EnumerateArray()
            .Select(static item => Convert.FromHexString(item.GetString()!.AsSpan(2))).ToArray();
        Account account = ExecutionProofVerifier.VerifyAccount(provider.Head.StateRoot, AccountAddress, accountProof);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("balance").GetString(), Is.EqualTo("0x7b"));
            Assert.That(result.GetProperty("storageProof")[0].GetProperty("key").GetString(), Is.EqualTo("0x1"));
            Assert.That(ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, slotProof), Is.EqualTo((UInt256)42));
        }
    }

    [Test]
    public async Task Hash_lookup_renders_only_authenticated_transactions_and_receipts([Values] bool receipt)
    {
        using Provider provider = new(new Account(7, 123));
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction transaction = new() { Nonce = 2, GasLimit = 21_000, GasPrice = 2, To = AccountAddress };
        new EthereumEcdsa(1).Sign(key, transaction, true);
        transaction.Hash = transaction.CalculateHash();
        provider.Transactions = [transaction];
        provider.Receipts = [new TxReceipt { TxType = transaction.Type, StatusCode = 1, GasUsedTotal = 21_000, Logs = [] }];
        provider.Header.TxRoot = TxTrie.CalculateRoot(provider.Transactions);
        provider.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(provider.Header), provider.Receipts, new ReceiptMessageDecoder());

        JsonElement result = (JsonElement)await provider.Rpc.InvokeAsync(receipt ? "eth_getTransactionReceipt" : "eth_getTransactionByHash",
            Parameters(transaction.Hash.ToString()), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty(receipt ? "transactionHash" : "hash").GetString(), Is.EqualTo(transaction.Hash.ToString()));
            Assert.That(result.GetProperty("blockHash").GetString(), Is.EqualTo(BlockHash.ToString()));
        }
    }

    [Test]
    public async Task Hash_lookup_finds_optimistic_ancestors_and_rejects_a_reorged_index_entry([Values] bool receipt)
    {
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction transaction = new() { GasLimit = 21_000, GasPrice = 2, To = AccountAddress };
        new EthereumEcdsa(1).Sign(key, transaction);
        transaction.Hash = transaction.CalculateHash();
        LookupSource source = new(transaction);
        using VerifiedRpc rpc = new(source, () => source.Finalized, 1, specProvider: MainnetSpecProvider.Instance, getLatestHead: () => source.Latest);
        string method = receipt ? "eth_getTransactionReceipt" : "eth_getTransactionByHash";
        JsonElement parameters = Parameters(transaction.Hash.ToString());

        JsonElement before = (JsonElement)await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        Assert.That(before.GetProperty("blockNumber").GetString(), Is.EqualTo(VerifiedRpc.Quantity(20_000_099UL)));
        source.Reorg();
        JsonElement after = (JsonElement)await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(source.CanonicalReads, Is.GreaterThan(0));
            Assert.That(source.BlockReads, Is.LessThanOrEqualTo(35));
        }
    }

    [Test]
    public async Task Fee_history_uses_receipt_gas_weighted_tips_and_shared_next_base_fee()
    {
        using Provider provider = new(new Account(7, 123), headNumber: 20_000_000);
        provider.Header.BaseFeePerGas = 100;
        provider.Header.GasUsed = 15_000_000;
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction first = new() { Nonce = 2, GasLimit = 21_000, GasPrice = 102, To = AccountAddress };
        Transaction second = new() { Nonce = 3, GasLimit = 63_000, GasPrice = 110, To = AccountAddress };
        new EthereumEcdsa(1).Sign(key, first, true);
        new EthereumEcdsa(1).Sign(key, second, true);
        first.Hash = first.CalculateHash();
        second.Hash = second.CalculateHash();
        provider.Transactions = [first, second];
        provider.Receipts = [new TxReceipt { TxType = first.Type, GasUsedTotal = 21_000, Logs = [] },
            new TxReceipt { TxType = second.Type, GasUsedTotal = 84_000, Logs = [] }];
        provider.Header.TxRoot = TxTrie.CalculateRoot(provider.Transactions);
        provider.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(provider.Header), provider.Receipts, new ReceiptMessageDecoder());

        JsonElement result = (JsonElement)await provider.Rpc.InvokeAsync("eth_feeHistory", Parameters("0x1", "finalized", new[] { 0, 25, 26, 100 }), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("baseFeePerGas").EnumerateArray().Select(static item => item.GetString()), Is.EqualTo(new[] { "0x64", "0x64" }));
            Assert.That(result.GetProperty("gasUsedRatio")[0].GetDouble(), Is.EqualTo(0.5));
            Assert.That(result.GetProperty("reward")[0].EnumerateArray().Select(static item => item.GetString()), Is.EqualTo(new[] { "0x2", "0x2", "0xa", "0xa" }));
            Assert.That(await provider.Rpc.InvokeAsync("eth_maxPriorityFeePerGas", Parameters(), CancellationToken.None), Is.EqualTo("0xa"));
            Assert.That(await provider.Rpc.InvokeAsync("eth_gasPrice", Parameters(), CancellationToken.None), Is.EqualTo("0x6e"));
        }
    }

    [TestCase("0x0", new double[] { 50 })]
    [TestCase("0x11", new double[] { 50 })]
    [TestCase("0x1", new double[] { 51, 50 })]
    [TestCase("0x1", new double[] { -1 })]
    [TestCase("0x1", new double[] { 101 })]
    public async Task Invalid_fee_history_is_rejected_before_peer_requests(string count, double[] percentiles)
    {
        using Provider provider = new(new Account(7, 123));
        await AssertRpcErrorAsync(provider, "eth_feeHistory", Parameters(count, "finalized", percentiles), -32602);
        Assert.That(provider.BlockFetches, Is.Zero);
    }

    [Test]
    public async Task Signed_submission_is_validated_locally_before_relay([Values("valid", "wrong-chain", "nonce", "balance", "trailing")] string scenario)
    {
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        using Provider provider = new(new Account(7, (UInt256)(scenario == "balance" ? 0 : 1_000_000_000)), headNumber: 20_000_000, accountAddress: key.Address);
        Transaction transaction = new() { Nonce = scenario == "nonce" ? 6UL : 7UL, GasLimit = 21_000, GasPrice = 2, To = AccountAddress, Value = 3 };
        new EthereumEcdsa(scenario == "wrong-chain" ? 2UL : 1UL).Sign(key, transaction, true);
        transaction.Hash = transaction.CalculateHash();
        byte[] encoded = TxDecoder.Instance.EncodeAsBytes(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        if (scenario == "trailing") encoded = [.. encoded, 0];
        JsonElement parameters = Parameters("0x" + Convert.ToHexString(encoded).ToLowerInvariant());
        if (scenario == "valid")
        {
            Assert.That(await provider.Rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None), Is.EqualTo(transaction.Hash.ToString()));
            Assert.That(provider.Broadcasts.Single().SenderAddress, Is.EqualTo(key.Address));
        }
        else
        {
            await AssertRpcErrorAsync(provider, "eth_sendRawTransaction", parameters, -32003);
            Assert.That(provider.Broadcasts, Is.Empty);
        }
    }

    [Test]
    public async Task Blob_submission_validates_kzg_sidecar_before_relay([Values] bool tamper)
    {
        await KzgPolynomialCommitments.InitializeAsync(Nethermind.Logging.LimboLogs.Instance.GetClassLogger<VerifiedRpcTests>());
        IBlobProofsManager proofs = IBlobProofsManager.For(ProofVersion.V1);
        ShardBlobNetworkWrapper wrapper = proofs.AllocateWrapper(new byte[131_072]);
        proofs.ComputeProofsAndCommitments(wrapper);
        byte[][] hashes = proofs.ComputeHashes(wrapper);
        if (tamper) wrapper.Proofs[0][0] ^= 1;
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        using Provider provider = new(new Account(7, (UInt256)1_000_000_000), headNumber: 20_000_000, accountAddress: key.Address);
        provider.Header.Timestamp = 1_800_000_000;
        provider.Header.ExcessBlobGas = 0;
        provider.Header.BlobGasUsed = 0;
        Transaction transaction = new()
        {
            Type = TxType.Blob,
            ChainId = 1,
            Nonce = 7,
            GasLimit = 21_000,
            DecodedMaxFeePerGas = 10,
            GasPrice = 2,
            MaxFeePerBlobGas = 2,
            To = Address.Zero,
            BlobVersionedHashes = hashes,
            NetworkWrapper = wrapper,
        };
        new EthereumEcdsa(1).Sign(key, transaction);
        transaction.Hash = transaction.CalculateHash();
        byte[] encoded = TxDecoder.Instance.EncodeAsBytes(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        JsonElement parameters = Parameters("0x" + Convert.ToHexString(encoded).ToLowerInvariant());
        if (tamper)
        {
            await AssertRpcErrorAsync(provider, "eth_sendRawTransaction", parameters, -32003);
            Assert.That(provider.Broadcasts, Is.Empty);
        }
        else
        {
            Assert.That(await provider.Rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None), Is.EqualTo(transaction.Hash.ToString()));
            Assert.That(provider.Broadcasts.Single().NetworkWrapper, Is.TypeOf<ShardBlobNetworkWrapper>());
        }
    }

    private sealed class LogRangeSource : IExecutionStateSource
    {
        private readonly Dictionary<ulong, Block> _blocks = [];
        private readonly Dictionary<Hash256, Block> _byHash = [];
        private readonly Dictionary<Hash256, TxReceipt[]> _receipts = [];
        private static readonly Hash256 Topic = Keccak.Compute("recent range log");
        internal readonly List<VerifiedHead> Anchors = [];
        internal readonly TaskCompletionSource AncestorsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? AncestorGate { get; init; }
        internal Func<BlockHeader, BlockHeader>? TransformHeader { get; set; }
        internal Func<Hash256[], Hash256[]>? TransformAncestors { get; set; }
        internal Func<ulong, CancellationToken, Task>? BeforeBlockRead { get; init; }
        internal Func<ulong, CancellationToken, Task>? BeforeReceiptRead { get; init; }
        internal readonly ConcurrentQueue<ulong> BodyNumbers = new();
        internal readonly Channel<ulong> ReceiptsCompleted = Channel.CreateUnbounded<ulong>();
        internal bool TamperReceipts { get; set; }
        internal int HeaderReads { get; private set; }
        private int _receiptReads;
        internal int ReceiptReads => Volatile.Read(ref _receiptReads);
        internal Block this[ulong number] => _blocks[number];

        internal LogRangeSource(ulong first = 300, ulong last = 325, bool signTransactions = false,
            Func<ulong, bool>? includeLogs = null)
        {
            using PrivateKey? key = signTransactions ? new("0x0000000000000000000000000000000000000000000000000000000000000001") : null;
            EthereumEcdsa signer = new(1);
            Hash256 parent = Keccak.Zero;
            for (ulong number = first; number <= last; number++)
            {
                Transaction[] transactions = new Transaction[2];
                TxReceipt[] receipts = new TxReceipt[2];
                for (int index = 0; index < 2; index++)
                {
                    Transaction transaction = new() { Nonce = (ulong)index, To = AccountAddress, Value = number, GasLimit = 21_000, GasPrice = 1 };
                    if (key is not null) signer.Sign(key, transaction, true);
                    transaction.Hash = transaction.CalculateHash();
                    transactions[index] = transaction;
                    receipts[index] = new TxReceipt
                    {
                        TxType = transaction.Type,
                        StatusCode = 1,
                        GasUsedTotal = (ulong)((index + 1) * 21_000),
                        Logs = includeLogs is not null && !includeLogs(number) ? []
                            : [new LogEntry(Address.Zero, [0xfe], [Topic]), new LogEntry(AccountAddress, [(byte)(number % 256), (byte)index], [Topic])],
                    };
                }
                BlockHeader header = Provider.CreateHeader(number, Keccak.Zero, Keccak.Compute("range state"));
                header.ParentHash = parent;
                header.TxRoot = TxTrie.CalculateRoot(transactions);
                header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(header), receipts, new ReceiptMessageDecoder());
                header.GasUsed = 42_000;
                header.Bloom = new Bloom(receipts.SelectMany(receipt => receipt.Logs!).ToArray());
                header.Hash = parent = Keccak.Compute(Rlp.Encode(header).Bytes);
                Block block = new(header, new BlockBody(transactions, [], []));
                _blocks.Add(number, block);
                _byHash.Add(header.Hash!, block);
                _receipts.Add(header.Hash!, receipts);
            }
        }

        internal VerifiedHead Head(ulong number) => new(100, number, _blocks[number].Hash!, _blocks[number].StateRoot!);

        internal void ReorgFrom(ulong first)
        {
            foreach (ulong number in _blocks.Keys.Where(number => number >= first).Order().ToArray())
            {
                Block before = _blocks[number];
                BlockHeader header = before.Header.Clone();
                if (number > first) header.ParentHash = _blocks[number - 1].Hash!;
                header.ExtraData = [1];
                Hash256 hash = Keccak.Compute(Rlp.Encode(header).Bytes);
                header.Hash = hash;
                Block block = new(header, before.Body);
                _blocks[number] = block;
                _byHash.Add(hash, block);
                _receipts.Add(hash, _receipts[before.Hash!]);
            }
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            if (_blocks[head.Number].Hash != head.BlockHash) throw new InvalidDataException("Unknown authenticated head.");
            return GetHeaderByHashAsync(head.BlockHash, cancellationToken);
        }

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HeaderReads++;
            BlockHeader header = _byHash[hash].Header;
            return Task.FromResult(TransformHeader?.Invoke(header) ?? header);
        }

        public async Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Anchors.Add(head);
            AncestorsStarted.TrySetResult();
            if (AncestorGate is not null) await AncestorGate.Task.WaitAsync(cancellationToken);
            if (head.Number - firstNumber > 256 || _blocks[head.Number].Hash != head.BlockHash)
                throw new InvalidDataException("Ancestry must be bounded and anchored to the pinned head.");
            Hash256[] hashes = new Hash256[checked((int)(head.Number - firstNumber + 1))];
            for (int index = 0; index < hashes.Length; index++)
            {
                BlockHeader header = _blocks[firstNumber + (ulong)index].Header;
                if (index > 0 && header.ParentHash != hashes[index - 1]) throw new InvalidDataException("Broken fixture ancestry.");
                hashes[index] = header.Hash!;
            }
            return TransformAncestors?.Invoke(hashes) ?? hashes;
        }

        public async Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BodyNumbers.Enqueue(header.Number);
            if (BeforeBlockRead is not null) await BeforeBlockRead(header.Number, cancellationToken);
            Block block = _byHash[header.Hash!];
            ExecutionPeerTransport.VerifyBlockBody(header, block.Body);
            return block;
        }

        public async Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _receiptReads);
            if (BeforeReceiptRead is not null) await BeforeReceiptRead(block.Number, cancellationToken);
            TxReceipt[] receipts = _receipts[block.Hash!];
            if (TamperReceipts)
            {
                receipts = [new TxReceipt(receipts[0]) { Logs = [new LogEntry(AccountAddress, [0xff], [Topic])] }, receipts[1]];
            }
            ExecutionPeerTransport.VerifyReceipts(block.Header, receipts, MainnetSpecProvider.Instance);
            ReceiptsCompleted.Writer.TryWrite(block.Number);
            return receipts;
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Provider : IExecutionStateSource, IDisposable
    {
        private readonly byte[] _accountLeaf;
        private readonly byte[] _storageLeaf;
        private readonly byte[] _code;
        private readonly SemaphoreSlim _snapSlots = new(8);
        private int _historicalReads;

        public Provider(Account account, byte[]? storageLeaf = null, byte[]? code = null, ulong headNumber = 10, Address? accountAddress = null)
        {
            _accountLeaf = Leaf(Keccak.Compute((accountAddress ?? AccountAddress).Bytes), AccountDecoder.Instance.EncodeAsBytes(account));
            _storageLeaf = storageLeaf ?? [];
            _code = code ?? [];
            Head = new VerifiedHead(100, headNumber, BlockHash, Keccak.Compute(_accountLeaf));
            LatestHead = Head;
            Header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                Head.Number, 30_000_000, 1_700_000_000, [])
            {
                Hash = BlockHash,
                StateRoot = Head.StateRoot,
                TxRoot = Keccak.EmptyTreeHash,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                WithdrawalsRoot = Keccak.EmptyTreeHash,
            };
            Rpc = CreateRpc();
        }

        public VerifiedRpc Rpc { get; }
        public VerifiedHead Head { get; }
        public VerifiedHead? LatestHead { get; set; }
        public ulong? CurrentSlot { get; set; }
        public VerifiedRpc CreateRpc(VerifiedCall? calls = null, Func<VerifiedHead>? getHead = null) => new(this, getHead ?? (() => Head), 1,
            calls, MainnetSpecProvider.Instance, () => LatestHead, head => head.IsFresh(CurrentSlot ?? head.Slot, 10));
        public BlockHeader Header { get; }
        public BlockHeader? HistoricalHeader { get; set; }
        public BlockHeader? OptimisticHeader { get; set; }
        public BlockHeader? CanonicalHistoricalHeader { get; private set; }
        public VerifiedHead? LastCanonicalAnchor { get; private set; }
        public TaskCompletionSource? HistoricalGate { get; init; }
        public TaskCompletionSource? HistoricalHeaderGate { get; init; }
        public int HistoricalReads => Volatile.Read(ref _historicalReads);
        public int HeaderByHashFetches { get; private set; }
        public int CanonicalHeaderFetches { get; private set; }
        public Transaction[] Transactions { get; set; } = [];
        public TxReceipt[] Receipts { get; set; } = [];
        public int BlockFetches { get; private set; }
        public int ReceiptFetches { get; private set; }
        public TaskCompletionSource? BlockGate { get; set; }
        public VerifiedHead? LastQueriedHead { get; private set; }
        public List<(string Kind, Hash256? BlockHash)> Requests { get; } = [];
        public List<Transaction> Broadcasts { get; } = [];
        public bool OmitProof { get; init; }
        public bool TamperProof { get; init; }
        public bool TamperStorageProof { get; init; }
        public bool Cancel { get; init; }

        public async Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            if (HistoricalGate is not null) await _snapSlots.WaitAsync(cancellationToken);
            try
            {
                if (HistoricalGate is not null && head.Number < Head.Number)
                {
                    Interlocked.Increment(ref _historicalReads);
                    await HistoricalGate.Task.WaitAsync(cancellationToken);
                }
                LastQueriedHead = head;
                Record("account", head.BlockHash, cancellationToken);
                return ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, OmitProof ? [] : [Served(_accountLeaf, TamperProof)]);
            }
            finally
            {
                if (HistoricalGate is not null) _snapSlots.Release();
            }
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            Record("storage", head.BlockHash, cancellationToken);
            byte[][] proof = _storageLeaf.Length == 0 ? [] : [Served(_storageLeaf, TamperStorageProof)];
            return Task.FromResult(ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, proof));
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            Record("code", null, cancellationToken);
            ExecutionProofVerifier.VerifyCode(account.CodeHash, _code);
            return Task.FromResult(_code);
        }

        public Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Broadcasts.Add(transaction);
            return Task.CompletedTask;
        }

        public Task<ExecutionAccountProof> GetProofAsync(VerifiedHead head, Address address, UInt256[] storageKeys, CancellationToken cancellationToken)
        {
            Record("proof", head.BlockHash, cancellationToken);
            LastQueriedHead = head;
            Account account = ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, [_accountLeaf]);
            ExecutionStorageProof[] slots = new ExecutionStorageProof[storageKeys.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                byte[][] witness = _storageLeaf.Length == 0 ? [] : [_storageLeaf];
                slots[i] = new(storageKeys[i], ExecutionProofVerifier.VerifyStorage(account.StorageRoot, storageKeys[i], witness), witness);
            }
            return Task.FromResult(new ExecutionAccountProof(account, [Served(_accountLeaf, TamperProof)], slots));
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            Record("header", head.BlockHash, cancellationToken);
            if (HistoricalHeader is { } historical && historical.Hash == head.BlockHash) return Task.FromResult(historical);
            if (OptimisticHeader is { } latest && latest.Hash == head.BlockHash) return Task.FromResult(latest);
            return Task.FromResult(Header);
        }
        public async Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken)
        {
            HeaderByHashFetches++;
            if (HistoricalHeaderGate is not null) await HistoricalHeaderGate.Task.WaitAsync(cancellationToken);
            return HistoricalHeader?.Hash == hash ? HistoricalHeader : throw new NotSupportedException();
        }
        public async Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken)
        {
            CanonicalHeaderFetches++;
            LastCanonicalAnchor = head;
            if (HistoricalHeaderGate is not null) await HistoricalHeaderGate.Task.WaitAsync(cancellationToken);
            BlockHeader? header = CanonicalHistoricalHeader ?? HistoricalHeader;
            if (OptimisticHeader is not null && head.Number > Head.Number
                && (head.BlockHash != OptimisticHeader.Hash || head.Number == number + 1 && OptimisticHeader.ParentHash != header?.Hash))
                throw new InvalidDataException("Optimistic ancestry was not anchored to the selected head.");
            return header?.Number == number ? header : throw new NotSupportedException();
        }
        public void ReorgOptimisticAncestor()
        {
            CanonicalHistoricalHeader = HistoricalHeader!.Clone();
            CanonicalHistoricalHeader.ExtraData = [1];
            CanonicalHistoricalHeader.Hash = Keccak.Compute(Rlp.Encode(CanonicalHistoricalHeader).Bytes);
            OptimisticHeader!.ParentHash = CanonicalHistoricalHeader.Hash;
            OptimisticHeader.Hash = Keccak.Compute(Rlp.Encode(OptimisticHeader).Bytes);
            LatestHead = new(102, OptimisticHeader.Number, OptimisticHeader.Hash, OptimisticHeader.StateRoot!);
        }
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            BlockFetches++;
            if (BlockGate is not null) await BlockGate.Task.WaitAsync(cancellationToken);
            BlockBody body = new(Transactions, [], []);
            ExecutionPeerTransport.VerifyBlockBody(header, body);
            return new Block(header, body);
        }
        public Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken)
        {
            ReceiptFetches++;
            ExecutionPeerTransport.VerifyReceipts(block.Header, Receipts, MainnetSpecProvider.Instance);
            return Task.FromResult(Receipts);
        }

        public static BlockHeader CreateHeader(ulong number, Hash256 hash, Hash256 stateRoot) => new(
            Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero, number, 30_000_000, 1_700_000_000, [])
        {
            Hash = hash,
            StateRoot = stateRoot,
            TxRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            WithdrawalsRoot = Keccak.EmptyTreeHash,
        };

        private void Record(string kind, Hash256? blockHash, CancellationToken cancellationToken)
        {
            if (Cancel) cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((kind, blockHash));
        }

        private static byte[] Served(byte[] node, bool tamper)
        {
            byte[] copy = (byte[])node.Clone();
            if (tamper) copy[^1] ^= 1;
            return copy;
        }

        public void Dispose()
        {
            Rpc.Dispose();
            _snapSlots.Dispose();
        }
    }

    private sealed class LookupSource : IExecutionStateSource
    {
        private readonly Transaction _transaction;
        private Dictionary<ulong, Block> _chain = [];
        public int BlockReads { get; private set; }
        public int CanonicalReads { get; private set; }
        public VerifiedHead Finalized => Head(20_000_090);
        public VerifiedHead Latest => Head(20_000_100);
        public LookupSource(Transaction transaction) { _transaction = transaction; BuildChain(includeTransaction: true); }
        public void Reorg() => BuildChain(includeTransaction: false);
        private VerifiedHead Head(ulong number)
        {
            BlockHeader header = _chain[number].Header;
            return new(0, number, header.Hash!, header.StateRoot!);
        }
        private void BuildChain(bool includeTransaction)
        {
            Dictionary<ulong, Block> chain = [];
            Hash256 parent = Keccak.Zero;
            for (ulong number = 20_000_000; number <= 20_000_100; number++)
            {
                Transaction[] transactions = includeTransaction && number == 20_000_099 ? [_transaction] : [];
                TxReceipt[] receipts = transactions.Length == 0 ? [] : [Receipt()];
                BlockHeader header = Provider.CreateHeader(number, Keccak.Zero, Keccak.EmptyTreeHash);
                header.ParentHash = parent;
                header.TxRoot = TxTrie.CalculateRoot(transactions);
                header.ReceiptsRoot = ReceiptTrie.CalculateRoot(MainnetSpecProvider.Instance.GetSpec(header), receipts, new ReceiptMessageDecoder());
                header.GasUsed = transactions.Length == 0 ? 0UL : 21_000;
                header.Hash = parent = Keccak.Compute(Rlp.Encode(header).Bytes);
                chain[number] = new(header, new BlockBody(transactions, [], []));
            }
            _chain = chain;
        }
        private TxReceipt Receipt() => new() { TxType = _transaction.Type, StatusCode = 1, GasUsedTotal = 21_000, Logs = [] };
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => Task.FromResult(_chain[head.Number].Header);
        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) => Task.FromResult(_chain.Values.Single(block => block.Hash == hash).Header);
        public Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken)
        {
            CanonicalReads++;
            if (_chain[head.Number].Hash != head.BlockHash) throw new InvalidDataException("Requested ancestor anchor changed.");
            return Task.FromResult(_chain[number].Header);
        }
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
        {
            if (_chain[head.Number].Hash != head.BlockHash) throw new InvalidDataException("Requested ancestor anchor changed.");
            Hash256[] result = new Hash256[checked((int)(head.Number - firstNumber + 1))];
            for (int i = 0; i < result.Length; i++) result[i] = _chain[firstNumber + (ulong)i].Hash!;
            return Task.FromResult(result);
        }
        public Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            BlockReads++;
            Block block = _chain[header.Number];
            ExecutionPeerTransport.VerifyBlockBody(header, block.Body);
            return Task.FromResult(block);
        }
        public Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken)
        {
            TxReceipt[] receipts = block.Transactions.Length == 0 ? [] : [Receipt()];
            ExecutionPeerTransport.VerifyReceipts(block.Header, receipts, MainnetSpecProvider.Instance);
            return Task.FromResult(receipts);
        }
        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
