// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    public async Task Account_values_ignore_forged_response_metadata(string method, string expected)
    {
        using Provider provider = new(new Account(7, 123));
        object actual = await provider.Rpc.InvokeAsync(method, Parameters(AccountAddress.ToString(), "finalized"), CancellationToken.None);

        Assert.That(actual, Is.EqualTo(expected));
        AssertPinnedRequest(provider.Requests[0], "eth_getProof");
    }

    [Test]
    public async Task Storage_value_is_derived_from_proof_and_padded_to_32_bytes()
    {
        UInt256 slot = 1;
        byte[] storageLeaf = Leaf(Keccak.Compute(slot.ToBigEndian()), Rlp.Encode((UInt256)42).Bytes);
        using Provider provider = new(new Account(7, 123, Keccak.Compute(storageLeaf), Keccak.OfAnEmptyString), storageLeaf);

        object actual = await provider.Rpc.InvokeAsync("eth_getStorageAt", Parameters(AccountAddress.ToString(), "0x1", "finalized"), CancellationToken.None);

        Assert.That(actual, Is.EqualTo("0x" + new string('0', 62) + "2a"));
        AssertPinnedRequest(provider.Requests[0], "eth_getProof");
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
        AssertPinnedRequest(provider.Requests[1], "eth_getCode");
    }

    [Test]
    public void Forged_account_proof_is_rejected([Values] bool omit)
    {
        using Provider provider = new(new Account(7, 123)) { OmitProof = omit, TamperProof = !omit };

        AssertRpcError(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), -32000);
    }

    [Test]
    public void Storage_rejects_wrong_key_or_tampered_proof([Values] bool wrongKey)
    {
        byte[] storageLeaf = Leaf(Keccak.Compute(((UInt256)1).ToBigEndian()), Rlp.Encode((UInt256)42).Bytes);
        using Provider provider = new(new Account(7, 123, Keccak.Compute(storageLeaf), Keccak.OfAnEmptyString), storageLeaf)
        {
            WrongStorageKey = wrongKey,
            TamperStorageProof = !wrongKey
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
        VerifiedRpc rpc = new(provider, () => provider.Head, 1);

        RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_blockNumber", Parameters(), CancellationToken.None));
        Assert.That(error!.Code, Is.EqualTo(-32001));
    }

    [Test]
    public async Task Stale_finality_does_not_block_exact_authenticated_optimistic_reads()
    {
        using Provider provider = new(new Account(7, 123));
        int finalizedReads = 0;
        VerifiedRpc rpc = new(provider, () =>
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
        VerifiedRpc rpc = new(provider, () => throw new RpcException(-32000, "Finality is stale."), 1,
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
    public async Task Transaction_receipt_and_logs_are_derived_from_verified_roots()
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
        provider.Header.GasUsed = receipt.GasUsedTotal;

        JsonElement tx = (JsonElement)await provider.Rpc.InvokeAsync("eth_getTransactionByBlockHashAndIndex",
            Parameters(BlockHash.ToString(), "0x0"), CancellationToken.None);
        JsonElement rpcReceipt = (JsonElement)await provider.Rpc.InvokeAsync("eth_getBlockReceipts", Parameters("finalized"), CancellationToken.None);
        JsonElement logs = (JsonElement)await provider.Rpc.InvokeAsync("eth_getLogs", Parameters(new
        {
            blockHash = BlockHash.ToString(),
            address = AccountAddress.ToString(),
            topics = new[] { topic.ToString() }
        }), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.GetProperty("hash").GetString(), Is.EqualTo(transaction.Hash!.ToString()));
            Assert.That(tx.GetProperty("from").GetString(), Is.EqualTo(key.Address.ToString()));
            Assert.That(rpcReceipt[0].GetProperty("transactionHash").GetString(), Is.EqualTo(transaction.Hash.ToString()));
            Assert.That(rpcReceipt[0].GetProperty("gasUsed").GetString(), Is.EqualTo("0x537f"));
            Assert.That(logs.GetArrayLength(), Is.EqualTo(1));
            Assert.That(logs[0].GetProperty("data").GetString(), Is.EqualTo("0xabcd"));
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

    [TestCase("eth_call", "[]", -32602)]
    [TestCase("eth_getBalance", "[]", -32602)]
    [TestCase("eth_getBalance", "[\"0x12\",\"finalized\"]", -32602)]
    [TestCase("eth_getStorageAt", "[\"0x1234567890123456789012345678901234567890\",\"0x01\",\"finalized\"]", -32602)]
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
    public void Malformed_or_failed_upstream_envelopes_are_rejected([Values("{", "{\"jsonrpc\":\"2.0\",\"id\":9,\"result\":{}}", "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000}}", "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}")] string response)
    {
        using Provider provider = new(new Account(7, 123)) { RawResponse = response };

        AssertRpcError(provider, "eth_getBalance", Parameters(AccountAddress.ToString(), "finalized"), -32000);
    }

    [Test]
    public async Task Snapshot_is_captured_once_before_fetch()
    {
        using Provider provider = new(new Account(7, 123));
        int reads = 0;
        VerifiedRpc rpc = new(provider, () => ++reads == 1 ? provider.Head : throw new InvalidOperationException("Snapshot read twice."), 1);

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

    private static void AssertPinnedRequest(JsonElement request, string method)
    {
        JsonElement parameters = request.GetProperty("params");
        JsonElement block = parameters[parameters.GetArrayLength() - 1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.GetProperty("method").GetString(), Is.EqualTo(method));
            Assert.That(block.GetProperty("blockHash").GetString(), Is.EqualTo(BlockHash.ToString()));
            Assert.That(block.GetProperty("requireCanonical").GetBoolean(), Is.True);
        }
    }

    private static JsonElement Parameters(params object[] values) => JsonSerializer.SerializeToElement(values);

    private sealed class Provider : HttpMessageHandler, IExecutionStateSource
    {
        private readonly byte[] _accountLeaf;
        private readonly byte[] _storageLeaf;
        private readonly byte[] _code;

        public Provider(Account account, byte[]? storageLeaf = null, byte[]? code = null)
        {
            _accountLeaf = Leaf(Keccak.Compute(AccountAddress.Bytes), AccountDecoder.Instance.EncodeAsBytes(account));
            _storageLeaf = storageLeaf ?? [];
            _code = code ?? [];
            Head = new VerifiedHead(100, 10, BlockHash, Keccak.Compute(_accountLeaf));
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
            Client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://execution.invalid/") };
            Rpc = new VerifiedRpc(this, () => Head, 1, specProvider: MainnetSpecProvider.Instance, getLatestHead: () => LatestHead);
        }

        public HttpClient Client { get; }
        public VerifiedRpc Rpc { get; }
        public VerifiedHead Head { get; }
        public VerifiedHead? LatestHead { get; set; }
        public BlockHeader Header { get; }
        public Transaction[] Transactions { get; set; } = [];
        public TxReceipt[] Receipts { get; set; } = [];
        public int BlockFetches { get; private set; }
        public VerifiedHead? LastQueriedHead { get; private set; }
        public List<JsonElement> Requests { get; } = [];
        public bool OmitProof { get; init; }
        public bool TamperProof { get; init; }
        public bool WrongStorageKey { get; init; }
        public bool TamperStorageProof { get; init; }
        public bool Cancel { get; init; }
        public string? RawResponse { get; init; }

        public async Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            LastQueriedHead = head;
            using JsonDocument response = await RequestAsync("eth_getProof", [address.ToString(), Array.Empty<string>(), Block(head)], cancellationToken);
            JsonElement proof = response.RootElement.GetProperty("result").GetProperty("accountProof");
            return ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, ReadProof(proof));
        }

        public async Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            using JsonDocument response = await RequestAsync("eth_getProof", [address.ToString(), new[] { key.ToBigEndian().ToHexString(withZeroX: true) }, Block(head)], cancellationToken);
            JsonElement entry = response.RootElement.GetProperty("result").GetProperty("storageProof")[0];
            if (entry.GetProperty("key").GetString() != "0x1") throw new InvalidDataException("Wrong key.");
            return ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, ReadProof(entry.GetProperty("proof")));
        }

        public async Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            using JsonDocument response = await RequestAsync("eth_getCode", [AccountAddress.ToString(), Block(Head)], cancellationToken);
            byte[] code = Convert.FromHexString(response.RootElement.GetProperty("result").GetString()![2..]);
            ExecutionProofVerifier.VerifyCode(account.CodeHash, code);
            return code;
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => Task.FromResult(Header);
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            BlockFetches++;
            BlockBody body = new(Transactions, [], []);
            ExecutionPeerTransport.VerifyBlockBody(header, body);
            return Task.FromResult(new Block(header, body));
        }
        public Task<TxReceipt[]> GetReceiptsAsync(Block block, CancellationToken cancellationToken)
        {
            ExecutionPeerTransport.VerifyReceipts(block.Header, Receipts, MainnetSpecProvider.Instance);
            return Task.FromResult(Receipts);
        }

        private static object Block(VerifiedHead head) => new { blockHash = head.BlockHash.ToString(), requireCanonical = true };

        private async Task<JsonDocument> RequestAsync(string method, object[] parameters, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await Client.PostAsync("", new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json"), cancellationToken);
            JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out JsonElement version) || version.GetString() != "2.0"
                || !root.TryGetProperty("id", out JsonElement id) || id.GetInt32() != 1 || root.TryGetProperty("error", out _)
                || !root.TryGetProperty("result", out JsonElement result) || result.ValueKind == JsonValueKind.Null)
            {
                document.Dispose();
                throw new InvalidDataException("Invalid response.");
            }
            return document;
        }

        private static byte[][] ReadProof(JsonElement proof) => proof.EnumerateArray()
            .Select(static node => Convert.FromHexString(node.GetString()![2..])).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Cancel) cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement rpc = document.RootElement.Clone();
            Requests.Add(rpc);
            byte[] accountLeaf = (byte[])_accountLeaf.Clone();
            byte[] storageLeaf = (byte[])_storageLeaf.Clone();
            if (TamperProof) accountLeaf[^1] ^= 1;
            if (TamperStorageProof) storageLeaf[^1] ^= 1;
            object result = rpc.GetProperty("method").GetString() == "eth_getCode"
                ? _code.ToHexString(withZeroX: true)
                : new
                {
                    balance = "0xffff",
                    nonce = "0xffff",
                    codeHash = BlockHash.ToString(),
                    storageHash = BlockHash.ToString(),
                    accountProof = OmitProof ? Array.Empty<string>() : new[] { accountLeaf.ToHexString(withZeroX: true) },
                    storageProof = new[] { new { key = WrongStorageKey ? "0x2" : "0x1", value = "0xffff", proof = storageLeaf.Length == 0 ? Array.Empty<string>() : new[] { storageLeaf.ToHexString(withZeroX: true) } } }
                };
            string json = RawResponse ?? JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Client.Dispose();
            base.Dispose(disposing);
        }
    }
}
