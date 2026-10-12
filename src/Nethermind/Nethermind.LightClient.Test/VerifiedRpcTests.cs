// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
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
            AssertRpcError(provider, "eth_getCode", Parameters(AccountAddress.ToString(), "finalized"), -32000);
        else
            Assert.That(await provider.Rpc.InvokeAsync("eth_getCode", Parameters(AccountAddress.ToString(), "finalized"), CancellationToken.None), Is.EqualTo("0x600100"));
        Assert.That(provider.Requests[1].Kind, Is.EqualTo("code"));
    }

    [Test]
    public void Forged_account_proof_is_rejected([Values] bool omit)
    {
        using Provider provider = new(new Account(7, 123)) { OmitProof = omit, TamperProof = !omit };

        AssertRpcError(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), -32000);
    }

    [Test]
    public void Storage_rejects_a_tampered_proof()
    {
        byte[] storageLeaf = Leaf(Keccak.Compute(((UInt256)1).ToBigEndian()), Rlp.Encode((UInt256)42).Bytes);
        using Provider provider = new(new Account(7, 123, Keccak.Compute(storageLeaf), Keccak.OfAnEmptyString), storageLeaf)
        {
            TamperStorageProof = true
        };

        AssertRpcError(provider, "eth_getStorageAt", Parameters(AccountAddress.ToString(), "0x1", "finalized"), -32000);
    }

    [Test]
    public void Unverified_selectors_are_rejected_before_network([Values("pending", "safe", "earliest", "0x9", "0x0a")] string selector)
    {
        using Provider provider = new(new Account(7, 123));
        RpcException? exception = Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), selector), CancellationToken.None));

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
    public void Latest_without_an_authenticated_optimistic_head_errors()
    {
        using Provider provider = new(new Account(7, 123));
        using VerifiedRpc rpc = new(provider, () => provider.Head, 1);

        RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo(-32001));
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
    public void Stale_finality_still_rejects_finalized_and_historical_reads()
    {
        using Provider provider = new(new Account(7, 123));
        using VerifiedRpc rpc = new(provider, () => throw new RpcException(-32000, "Finality is stale."), 1,
            specProvider: MainnetSpecProvider.Instance, getLatestHead: () => provider.Head);

        (string method, JsonElement parameters)[] requests =
        [
            ("eth_getBalance", Parameters(AccountAddress.ToString(), "finalized")),
            ("eth_getBalance", Parameters(AccountAddress.ToString(), "0x9")),
            ("eth_getBlockByNumber", Parameters("finalized", false)),
            ("eth_getLogs", Parameters(new { fromBlock = "finalized", toBlock = "finalized" })),
        ];
        foreach ((string method, JsonElement parameters) in requests)
        {
            RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method, parameters, CancellationToken.None));
            Assert.That(error!.Message, Is.EqualTo("Finality is stale."), method);
        }
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
        AssertRpcError(provider, "eth_getBlockReceipts", Parameters("finalized"), -32000);
    }

    [Test]
    public void Invalid_log_filters_reject_before_block_fetch()
    {
        using Provider provider = new(new Account(7, 123));

        AssertRpcError(provider, "eth_getLogs", Parameters(new { blockHash = BlockHash.ToString(), fromBlock = "finalized" }), -32602);
        AssertRpcError(provider, "eth_getLogs", Parameters(new { topics = new string[5] }), -32602);
        AssertRpcError(provider, "eth_getLogs", Parameters(new { address = "0x12" }), -32602);
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
        RpcException? error = Assert.ThrowsAsync<RpcException>(async () =>
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
            RpcException? busy = Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.InvokeAsync("eth_getBlockByNumber",
                Parameters("finalized", false), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.That(busy!.Code, Is.EqualTo(-32005));
            if (cancel) cancellation.Cancel();
            else provider.BlockGate!.SetException(new InvalidDataException("Invalid authenticated peer body."));
            foreach (Task<object> task in active)
            {
                if (cancel) Assert.CatchAsync<OperationCanceledException>(async () => await task);
                else Assert.That(Assert.ThrowsAsync<RpcException>(async () => await task)!.Code, Is.EqualTo(-32000));
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
    public async Task Current_number_and_hash_selectors_are_accepted([Values(0, 1, 2, 3)] int selectorKind)
    {
        object selector = selectorKind switch
        {
            0 => "0xa",
            1 => new { blockNumber = "0xa" },
            2 => new { blockHash = BlockHash.ToString(), requireCanonical = true },
            _ => BlockHash.ToString()
        };
        using Provider provider = new(new Account(7, 123));

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
            AssertRpcError(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), selector), -32001);

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
        AssertRpcError(provider, method, ParametersFor(new { blockHash = provider.HistoricalHeader.Hash!.ToString(), requireCanonical = true }), -32001);
        Assert.That(await provider.Rpc.InvokeAsync(method, ParametersFor("latest"), CancellationToken.None), Is.Not.Null,
            "Rejecting an orphan must release the historical request admission slot.");
    }

    [Test]
    public async Task Pinned_optimistic_block_and_relay_header_are_anchored_to_native_latest([Values] bool byHash)
    {
        using Provider provider = OptimisticProvider();
        Hash256 hash = provider.HistoricalHeader!.Hash!;
        JsonElement block = (JsonElement)await provider.Rpc.InvokeAsync(byHash ? "eth_getBlockByHash" : "eth_getBlockByNumber",
            Parameters(byHash ? hash.ToString() : $"0x{provider.HistoricalHeader.Number:x}", false), CancellationToken.None);
        byte[] header = await provider.Rpc.GetAuthenticatedHeaderRlpAsync(hash, CancellationToken.None);
        Assert.That(block.GetProperty("hash").GetString(), Is.EqualTo(hash.ToString()));
        Assert.That(Keccak.Compute(header), Is.EqualTo(hash));
        Assert.That(provider.LastCanonicalAnchor, Is.EqualTo(provider.LatestHead));

        provider.ReorgOptimisticAncestor();
        AssertRpcError(provider, "eth_getBlockByHash", Parameters(hash.ToString(), false), -32001);
        Assert.That(Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.GetAuthenticatedHeaderRlpAsync(hash, CancellationToken.None))!.Code, Is.EqualTo(-32001));
    }

    [Test]
    public async Task Optimistic_state_history_retains_the_32_ancestor_bound([Values(32, 33)] int depth)
    {
        using Provider provider = OptimisticProvider();
        provider.HistoricalHeader = Provider.CreateHeader(provider.LatestHead!.Number - (ulong)depth, Keccak.Compute("bounded optimistic history"), provider.Head.StateRoot);
        if (depth == 32)
            Assert.That(await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), $"0x{provider.HistoricalHeader.Number:x}"), CancellationToken.None), Is.EqualTo("0x7b"));
        else AssertRpcError(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), $"0x{provider.HistoricalHeader.Number:x}"), -32001);
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
                RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rejected);
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
        Assert.CatchAsync<OperationCanceledException>(async () => await interrupted);
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
    public void Invalid_requests_do_not_reach_provider(string method, string json, int error)
    {
        using Provider provider = new(new Account(7, 123));
        using JsonDocument parameters = JsonDocument.Parse(json);

        AssertRpcError(provider, method, parameters.RootElement, error);
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
    public void Cancellation_reaches_execution_fetch()
    {
        using Provider provider = new(new Account(7, 123)) { Cancel = true };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await provider.Rpc.InvokeAsync("eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), cancellation.Token));
    }

    private static void AssertRpcError(Provider provider, string method, JsonElement parameters, int code)
    {
        RpcException? exception = Assert.ThrowsAsync<RpcException>(async () => await provider.Rpc.InvokeAsync(method, parameters, CancellationToken.None));
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
        if (tamper) { AssertRpcError(provider, "eth_getProof", parameters, -32000); return; }
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
    public void Invalid_fee_history_is_rejected_before_peer_requests(string count, double[] percentiles)
    {
        using Provider provider = new(new Account(7, 123));
        AssertRpcError(provider, "eth_feeHistory", Parameters(count, "finalized", percentiles), -32602);
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
            AssertRpcError(provider, "eth_sendRawTransaction", parameters, -32003);
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
            AssertRpcError(provider, "eth_sendRawTransaction", parameters, -32003);
            Assert.That(provider.Broadcasts, Is.Empty);
        }
        else
        {
            Assert.That(await provider.Rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None), Is.EqualTo(transaction.Hash.ToString()));
            Assert.That(provider.Broadcasts.Single().NetworkWrapper, Is.TypeOf<ShardBlobNetworkWrapper>());
        }
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
            Rpc = new VerifiedRpc(this, () => Head, 1, specProvider: MainnetSpecProvider.Instance, getLatestHead: () => LatestHead);
        }

        public VerifiedRpc Rpc { get; }
        public VerifiedHead Head { get; }
        public VerifiedHead? LatestHead { get; set; }
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
