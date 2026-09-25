// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// End-to-end tests of <c>explain_transaction</c>, <c>simulate_transaction</c> and <c>block_summary</c> against a test
/// chain holding a transfer, a reverting call, an out-of-gas call, an ERC-20-shaped Transfer event, a value forwarder
/// and a self-recursive contract (see <see cref="McpTxScenario"/>).
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpTransactionToolsTests
{
    private const int BatchIds = 20;
    private static readonly string UnknownHash = Keccak.Compute("unknown transaction").ToString();

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private McpTxScenario _scenario = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // A small data limit keeps the over-limit request below the HTTP body limit.
        _node = await McpTestNode.Create(c =>
        {
            c.MaxConcurrentToolCalls = 64;
            c.MaxCallDataSize = 1024;
        });
        _scenario = await McpTxScenario.Create(_node);
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [Test]
    public async Task Explain_native_transfer()
    {
        JsonElement result = await Success("explain_transaction", ("hash", _scenario.Transfer.Hash!.ToString()));

        JsonElement fees = result.GetProperty("fees");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(result.GetProperty("from").GetString(), Is.EqualTo(TestItem.AddressB.ToString(true, true)));
            Assert.That(result.GetProperty("to").GetString(), Is.EqualTo(TestItem.AddressC.ToString(true, true)));
            Assert.That(result.GetProperty("value").GetProperty("formatted").GetString(), Is.EqualTo("1.5"));
            Assert.That(result.GetProperty("value").GetProperty("symbol").GetString(), Is.EqualTo("ETH"));
            Assert.That(result.GetProperty("value").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(McpTxScenario.TransferValue)));
            Assert.That(result.GetProperty("type").GetProperty("name").GetString(), Is.EqualTo("legacy"));
            Assert.That(result.GetProperty("block").GetProperty("number").GetUInt64(), Is.EqualTo(_scenario.Block.Number));
            Assert.That(result.GetProperty("block").GetProperty("timestampIso").GetString(), Does.EndWith("Z"));
            Assert.That(fees.GetProperty("gasUsed").GetUInt64(), Is.EqualTo(GasCostOf.Transaction));
            Assert.That(fees.GetProperty("gasUsedPercent").GetDouble(), Is.EqualTo(100));
            // The test chain is pre-London: the whole fee (gas price 1 wei) goes to the block producer.
            Assert.That(fees.GetProperty("total").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(GasCostOf.Transaction)));
            Assert.That(fees.TryGetProperty("baseFee", out _), Is.False);
            Assert.That(result.TryGetProperty("method", out _), Is.False);
            Assert.That(result.TryGetProperty("failure", out _), Is.False);
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("sent 1.5 ETH to").And.Contain("succeeded in block"));
        }
    }

    [Test]
    public async Task Explain_revert_with_error_string_decodes_reason_and_frame()
    {
        JsonElement result = await Success("explain_transaction", ("hash", _scenario.RevertCall.Hash!.ToString()));

        JsonElement failure = result.GetProperty("failure");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("failed"));
            Assert.That(result.GetProperty("method").GetProperty("name").GetString(), Is.EqualTo("transfer"));
            Assert.That(failure.GetProperty("outOfGas").GetBoolean(), Is.False);
            Assert.That(failure.GetProperty("frame").GetProperty("depth").GetInt32(), Is.Zero);
            Assert.That(failure.GetProperty("frame").GetProperty("to").GetString(), Is.EqualTo(_scenario.RevertContract.ToString(true, true)));
            Assert.That(failure.GetProperty("revert").GetProperty("kind").GetString(), Is.EqualTo("Error"));
            Assert.That(failure.GetProperty("revert").GetProperty("message").GetString(), Is.EqualTo(McpTxScenario.RevertReason));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("FAILED").And.Contain(McpTxScenario.RevertReason));
        }
    }

    [Test]
    public async Task Explain_out_of_gas()
    {
        JsonElement result = await Success("explain_transaction", ("hash", _scenario.OutOfGasCall.Hash!.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("failed"));
            Assert.That(result.GetProperty("failure").GetProperty("outOfGas").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("fees").GetProperty("gasUsed").GetUInt64(), Is.EqualTo(McpTxScenario.OutOfGasLimit));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("out of gas"));
        }
    }

    [Test]
    public async Task Explain_token_transfer_decodes_movement_and_net_flows()
    {
        JsonElement result = await Success("explain_transaction", ("hash", _scenario.TokenCall.Hash!.ToString()));

        JsonElement transfers = result.GetProperty("tokenTransfers");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(result.GetProperty("logCount").GetInt32(), Is.EqualTo(1));
            Assert.That(transfers.GetArrayLength(), Is.EqualTo(1));
            Assert.That(transfers[0].GetProperty("token").GetString(), Is.EqualTo(_scenario.TokenContract.ToString(true, true)));
            Assert.That(transfers[0].GetProperty("from").GetString(), Is.EqualTo(TestItem.AddressB.ToString(true, true)));
            Assert.That(transfers[0].GetProperty("to").GetString(), Is.EqualTo(TestItem.AddressD.ToString(true, true)));
            Assert.That(transfers[0].GetProperty("amount").GetString(), Is.EqualTo(McpTxScenario.TokenAmount.ToString()));
            Assert.That(result.GetProperty("netTokenFlows").GetArrayLength(), Is.EqualTo(2));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("sent " + McpTxScenario.TokenAmount));
        }
    }

    [Test]
    public void Blob_fee_is_part_of_the_total_and_shown_separately()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        LegacyTransactionForRpc tx = new() { Gas = 100_000, GasPrice = 10 };
        ReceiptForRpc receipt = new() { GasUsed = 21_000, EffectiveGasPrice = 10, BlobGasUsed = 131_072, BlobGasPrice = 3 };

        JsonObject fees = tools.Fees(tx, receipt, header: null, blockNumber: 1, timestamp: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fees["total"]!["wei"]!.GetValue<string>(), Is.EqualTo(McpAssert.Hex(210_000 + 393_216UL)));
            Assert.That(fees["executionFee"]!["wei"]!.GetValue<string>(), Is.EqualTo(McpAssert.Hex(210_000UL)));
            Assert.That(fees["blob"]!["fee"]!["wei"]!.GetValue<string>(), Is.EqualTo(McpAssert.Hex(393_216UL)));
            Assert.That(McpTransactionTools.PaidFee(tx, receipt), Is.EqualTo((UInt256)(210_000 + 393_216)));
        }
    }

    [Test]
    public void Summary_names_the_called_contracts_swap_when_the_sender_moved_no_tokens()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        Address bot = new("0x51C72848c68a965f66FA7a88855F9f7784502a7F");
        Address pool = new("0x88e6A0c2dDD26FEEb64F039a2c41296FcB3f5640");
        Address usdc = new("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48");
        Address weth = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
        Address fake = new("0x00000000000000000000000000000000000fa4e1");
        List<McpTokenMovement> movements =
        [
            new(usdc, "ERC-20", bot, pool, 33_522_696_356, null),
            new(weth, "ERC-20", pool, bot, UInt256.Parse("12450000000000000000"), null),
            new(fake, "ERC-20", pool, bot, 5, null),
        ];
        Dictionary<AddressAsKey, McpTokenInfo> tokens = new()
        {
            [usdc] = new McpTokenInfo(usdc, "USD Coin", "USDC", 6),
            [weth] = new McpTokenInfo(weth, "Wrapped Ether", "WETH", 18),
            [fake] = new McpTokenInfo(fake, "Tether", "USDT", 0),
        };
        StringBuilder summary = new();

        tools.AppendContractFlows(summary, bot, movements, tokens);

        string text = summary.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.StartWith("; "));
            Assert.That(text, Does.Contain("swapped 33,522.6963 USDC for 12.45 WETH and 5 USDT (unverified token"));
        }
    }

    [Test]
    public void Explain_trace_gets_half_the_tool_timeout_and_never_runs_past_85_percent()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        TimeSpan timeout = TimeSpan.FromMilliseconds(_node.Config.ToolTimeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tools.TraceTimeout(TimeSpan.Zero), Is.EqualTo(timeout * 0.5));
            Assert.That(tools.TraceTimeout(timeout * 0.6), Is.EqualTo(timeout * 0.85 - timeout * 0.6));
            Assert.That(tools.TraceTimeout(timeout * 0.9), Is.LessThan(TimeSpan.Zero));
        }
    }

    [Test]
    public void Token_lookups_stop_when_the_time_budget_is_spent()
    {
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        Address[] tokens = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressA];

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(new McpTokenMetadata(), eth, tokens, 10, CancellationToken.None, static () => true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.Empty);
            Assert.That(skipped, Is.EqualTo(2));
            Assert.That(eth.ReceivedCalls(), Is.Empty, "no metadata call is made once the budget is spent");
        }
    }

    [Test]
    public void Token_metadata_cache_expires_so_a_changed_token_is_read_again()
    {
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_chainId().Returns(ResultWrapper<ulong>.Success(1));
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(static _ => ResultWrapper<byte[]>.Success([0x60]));
        string symbol = "OLD";
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(_ => ResultWrapper<HexBytes>.Success(new HexBytes(TestContracts.AbiString(symbol))));
        TimeProvider time = Substitute.For<TimeProvider>();
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        time.GetUtcNow().Returns(_ => now);
        McpTokenMetadata metadata = new(LimboLogs.Instance, timeProvider: time);

        string? first = metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol;
        symbol = "NEW";
        string? cached = metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol;
        now += McpTokenMetadata.CacheTtl;
        string? expired = metadata.Get(eth, TestItem.AddressA, BlockParameter.Latest)?.Symbol;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo("OLD"));
            Assert.That(cached, Is.EqualTo("OLD"), "served from the cache within its lifetime");
            Assert.That(expired, Is.EqualTo("NEW"), "an upgraded token is read again once the entry expires");
            Assert.That(metadata.CachedCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Erc1155_batch_yields_a_movement_for_every_id()
    {
        List<McpTokenMovement> movements = [];

        McpTxTokens.Extract(TransferBatchLog(BatchIds).ToLogEntry(), movements, wrappedNativeToken: null);

        Assert.That(movements.Select(static m => m.TokenId), Is.EqualTo(Enumerable.Range(1, BatchIds).Select(static i => (UInt256?)(ulong)i)));
    }

    [Test]
    public void Block_receipt_stats_count_every_batch_id_and_leave_pre_byzantium_failures_unknown()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        ReceiptForRpc[] receipts =
        [
            new() { Root = Keccak.Zero, GasUsed = 50_000, EffectiveGasPrice = 1, Logs = [TransferBatchLog(BatchIds)] },
            new() { Root = Keccak.Zero, GasUsed = 21_000, EffectiveGasPrice = 1, Logs = [] }
        ];
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<ReceiptForRpc[]?>.Success(receipts));
        List<string> notes = [];

        JsonObject stats = tools.ReceiptStats(eth, new BlockParameter(1), null, notes, static () => true, CancellationToken.None)!;

        JsonNode topToken = stats["topTokens"]![0]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.ContainsKey("failed") && stats["failed"] is null, Is.True, "pre-Byzantium receipts have no status, so failures are unknown");
            Assert.That(stats["tokenTransfers"]!.GetValue<int>(), Is.EqualTo(BatchIds));
            Assert.That(topToken["transfers"]!.GetValue<int>(), Is.EqualTo(BatchIds));
            Assert.That(topToken["symbol"], Is.Null, "no metadata is read once the token budget is spent");
            Assert.That(notes, Has.Some.Contains("predate Byzantium"));
            Assert.That(notes, Has.Some.Contains("shown by address only"));
            Assert.That(eth.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(IEthRpcModule.eth_getCode)), Is.Zero);
        }
    }

    [Test]
    public async Task Explain_lists_internal_native_transfers()
    {
        JsonElement result = await Success("explain_transaction", ("hash", _scenario.ForwardCall.Hash!.ToString()));

        JsonElement internals = result.GetProperty("internalTransfers");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("internalTransfersTotal").GetInt32(), Is.EqualTo(1));
            Assert.That(internals[0].GetProperty("type").GetString(), Is.EqualTo("CALL"));
            Assert.That(internals[0].GetProperty("from").GetString(), Is.EqualTo(_scenario.Forwarder.ToString(true, true)));
            Assert.That(internals[0].GetProperty("to").GetString(), Is.EqualTo(TestItem.AddressC.ToString(true, true)));
            Assert.That(internals[0].GetProperty("value").GetString(), Is.EqualTo(McpAssert.Hex(McpTxScenario.ForwardValue)));
            Assert.That(internals[0].GetProperty("depth").GetInt32(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Explain_contract_creation_reports_created_address()
    {
        JsonElement result = await Success("explain_transaction", ("hash", _scenario.RevertDeploy.Hash!.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TryGetProperty("to", out _), Is.False);
            Assert.That(result.GetProperty("contractCreated").GetString(), Is.EqualTo(_scenario.RevertContract.ToString(true, true)));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("deployed a contract"));
        }
    }

    [Test]
    public async Task Explain_pending_transaction_from_the_pool()
    {
        PrivateKey sender = TestItem.PrivateKeyC;
        ulong nonce = _node.Chain.WorldStateManager.GlobalStateReader.GetNonce(_node.Chain.BlockTree.Head!.Header, sender.Address);
        Transaction pending = Build.A.Transaction.WithChainId(_node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(2)
            .WithTo(TestItem.AddressD).WithValue(7).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(_node.Chain.EthereumEcdsa, sender).TestObject;
        _node.Chain.AddTransactions(pending);

        JsonElement result = await Success("explain_transaction", ("hash", pending.Hash!.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("pending"));
            Assert.That(result.TryGetProperty("block", out _), Is.False);
            Assert.That(result.GetProperty("fees").GetProperty("maxFee").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(2 * GasCostOf.Transaction)));
            Assert.That(result.GetProperty("summary").GetString(), Does.StartWith("Pending in the mempool"));
        }
    }

    [Test]
    public async Task Explain_unknown_hash_is_not_found()
    {
        JsonElement error = McpAssert.Error(await Call("explain_transaction", ("hash", UnknownHash)), McpAssert.NotFound);
        McpAssert.NoInternals(error);
    }

    [TestCase("explain_transaction", "hash", "0x1234")]
    [TestCase("block_summary", "block", "pending")]
    [TestCase("block_summary", "block", "banana")]
    public async Task Invalid_input(string tool, string name, string value) =>
        McpAssert.Error(await Call(tool, (name, value)), McpAssert.InvalidInput);

    [Test]
    public async Task Simulate_token_call_decodes_logs_and_transfers()
    {
        JsonElement result = await Success("simulate_transaction",
            ("from", TestItem.AddressB.ToString()),
            ("to", _scenario.TokenContract.ToString()),
            ("data", McpTxScenario.TransferCallData.ToHexString(true)));

        JsonElement call = result.GetProperty("calls")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(result.GetProperty("broadcast").GetBoolean(), Is.False);
            Assert.That(result.GetProperty("simulatedBlockNumber").GetUInt64(), Is.EqualTo(_node.Chain.BlockTree.Head!.Number + 1));
            Assert.That(call.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(call.GetProperty("method").GetString(), Is.EqualTo("transfer(address,uint256)"));
            Assert.That(call.GetProperty("gasUsed").GetUInt64(), Is.GreaterThan(GasCostOf.Transaction));
            Assert.That(call.GetProperty("logs").GetArrayLength(), Is.EqualTo(1));
            Assert.That(call.GetProperty("tokenTransfers")[0].GetProperty("amount").GetString(), Is.EqualTo(McpTxScenario.TokenAmount.ToString()));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("would succeed").And.Contain("Nothing was broadcast"));
        }
    }

    [Test]
    public async Task Simulate_value_transfer_reports_native_transfer()
    {
        JsonElement result = await Success("simulate_transaction",
            ("from", TestItem.AddressB.ToString()),
            ("to", TestItem.AddressD.ToString()),
            ("value", "1000000000000000000"));

        JsonElement native = result.GetProperty("calls")[0].GetProperty("nativeTransfers");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(native[0].GetProperty("to").GetString(), Is.EqualTo(TestItem.AddressD.ToString(true, true)));
            Assert.That(native[0].GetProperty("valueFormatted").GetString(), Is.EqualTo("1"));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("moving 1 ETH"));
        }
    }

    [Test]
    public async Task Simulate_revert_decodes_reason()
    {
        JsonElement result = await Success("simulate_transaction",
            ("to", _scenario.RevertContract.ToString()),
            ("data", "0x"));

        JsonElement call = result.GetProperty("calls")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("reverted"));
            Assert.That(call.GetProperty("status").GetString(), Is.EqualTo("reverted"));
            Assert.That(call.GetProperty("error").GetProperty("revert").GetProperty("message").GetString(), Is.EqualTo(McpTxScenario.RevertReason));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("would fail"));
        }
    }

    [Test]
    public async Task Simulate_call_sequence_runs_in_order()
    {
        string calls = $$"""
            [{"to":"{{_scenario.TokenContract}}","data":"{{McpTxScenario.TransferCallData.ToHexString(true)}}"},
             {"to":"{{_scenario.RevertContract}}"}]
            """;
        JsonElement result = await Success("simulate_transaction",
            ("from", TestItem.AddressB.ToString()),
            ("calls", JsonDocument.Parse(calls).RootElement));

        JsonElement results = result.GetProperty("calls");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.GetArrayLength(), Is.EqualTo(2));
            Assert.That(results[0].GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(results[1].GetProperty("status").GetString(), Is.EqualTo("reverted"));
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("reverted"));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("would fail at call 1"));
        }
    }

    [Test]
    public async Task Simulate_with_code_override_runs_the_overridden_code()
    {
        Address empty = TestItem.AddressF;
        string overrides = $$$"""{"{{{empty}}}":{"code":"{{{McpTxScenario.RevertRuntimeCode.ToHexString(true)}}}","balance":"0x1"}}""";
        JsonElement result = await Success("simulate_transaction",
            ("to", empty.ToString()),
            ("stateOverrides", JsonDocument.Parse(overrides).RootElement));

        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("reverted"));
    }

    [Test]
    public async Task Simulate_limits_and_bad_input()
    {
        string to = _scenario.TokenContract.ToString();
        string nineCalls = "[" + string.Join(",", Enumerable.Repeat($$"""{"to":"{{to}}"}""", 9)) + "]";
        StringBuilder nineAccounts = new("{");
        for (int i = 0; i < 9; i++) nineAccounts.Append(i == 0 ? "" : ",").Append($$"""
            "0x{{i + 1:x40}}":{"balance":"0x1"}
            """);
        nineAccounts.Append('}');

        (string, object?)[][] cases =
        [
            [("to", to), ("gas", (_node.Config.MaxCallGas + 1).ToString())],
            [("to", to), ("gas", "0")],
            [("to", to), ("data", "0x" + new string('a', 2 * (_node.Config.MaxCallDataSize + 1)))],
            [("calls", JsonDocument.Parse(nineCalls).RootElement)],
            [("to", to), ("calls", JsonDocument.Parse($$"""[{"to":"{{to}}"}]""").RootElement)],
            [("calls", JsonDocument.Parse($$"""[{"to":"{{to}}","nonce":"0x1"}]""").RootElement)],
            [("to", to), ("stateOverrides", JsonDocument.Parse(nineAccounts.ToString()).RootElement)],
            [("to", to), ("stateOverrides", JsonDocument.Parse($$$$"""{"{{{{to}}}}":{"state":{}}}""").RootElement)],
            [("to", to), ("stateOverrides", JsonDocument.Parse($$$"""{"{{{to}}}":{"movePrecompileToAddress":"{{{to}}}"}}""").RootElement)],
            [("data", "0x")],
            [("to", to), ("block", "pending")],
        ];

        foreach ((string, object?)[] args in cases)
        {
            JsonElement error = McpAssert.Error(await Call("simulate_transaction", args), McpAssert.InvalidInput);
            McpAssert.NoInternals(error);
        }
    }

    [Test]
    public async Task Simulate_unknown_block_is_not_found() =>
        McpAssert.Error(await Call("simulate_transaction", ("to", _scenario.TokenContract.ToString()), ("block", "0xffffff")), McpAssert.NotFound);

    [Test]
    public async Task Block_summary_of_the_scenario_block()
    {
        JsonElement result = await Success("block_summary", ("block", _scenario.Block.Number.ToString()));

        JsonElement receipts = result.GetProperty("receipts");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("number").GetUInt64(), Is.EqualTo(_scenario.Block.Number));
            Assert.That(result.GetProperty("hash").GetString(), Is.EqualTo(_scenario.Block.Hash!.ToString()));
            Assert.That(result.GetProperty("transactionCount").GetInt32(), Is.EqualTo(_scenario.Block.Transactions.Length));
            Assert.That(result.GetProperty("transactionTypes").GetProperty("legacy").GetInt32(), Is.EqualTo(_scenario.Block.Transactions.Length));
            Assert.That(result.GetProperty("contractCreations").GetInt32(), Is.Zero);
            Assert.That(result.GetProperty("gasUsed").GetUInt64(), Is.EqualTo(_scenario.Block.GasUsed));
            Assert.That(result.GetProperty("topRecipients").GetArrayLength(), Is.EqualTo(_scenario.Block.Transactions.Length));
            Assert.That(receipts.GetProperty("failed").GetInt32(), Is.EqualTo(2));
            Assert.That(receipts.GetProperty("tokenTransfers").GetInt32(), Is.EqualTo(1));
            Assert.That(receipts.GetProperty("topTokens")[0].GetProperty("token").GetString(), Is.EqualTo(_scenario.TokenContract.ToString(true, true)));
            Assert.That(result.GetProperty("summary").GetString(), Does.StartWith($"Block {_scenario.Block.Number}").And.Contain("2 failed"));
        }
    }

    [Test]
    public async Task Block_summary_by_hash_and_tag()
    {
        JsonElement byHash = await Success("block_summary", ("block", _scenario.Block.Hash!.ToString()));
        JsonElement genesis = await Success("block_summary", ("block", "earliest"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byHash.GetProperty("number").GetUInt64(), Is.EqualTo(_scenario.Block.Number));
            Assert.That(genesis.GetProperty("number").GetUInt64(), Is.Zero);
            Assert.That(genesis.GetProperty("transactionCount").GetInt32(), Is.Zero);
        }
    }

    [Test]
    public async Task Block_summary_unknown_block_is_not_found() =>
        McpAssert.Error(await Call("block_summary", ("block", "0xffffff")), McpAssert.NotFound);

    [TestCase("explain_transaction")]
    [TestCase("simulate_transaction")]
    [TestCase("block_summary")]
    public async Task Tool_declares_an_output_schema_and_is_read_only(string toolName)
    {
        McpClientTool tool = (await _client.ListToolsAsync()).Single(t => t.Name == toolName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null);
            Assert.That(tool.ProtocolTool.Annotations?.ReadOnlyHint, Is.True);
            Assert.That(tool.Description, Does.Contain("xDAI"));
        }
    }

    private async Task<JsonElement> Success(string toolName, params (string Name, object? Value)[] args)
    {
        CallToolResult result = await Call(toolName, args);
        JsonElement value = McpAssert.Success(result);
        await McpToolCalls.AssertConformsToOutputSchema(_client, toolName, result);
        return value;
    }

    private Task<CallToolResult> Call(string toolName, params (string Name, object? Value)[] args) => McpToolCalls.Call(_client, toolName, args);

    // An ERC-1155 TransferBatch of ids 1..count, each moving 10 units, from AddressA to AddressB.
    private static LogEntryForRpc TransferBatchLog(int count)
    {
        int[] ids = [.. Enumerable.Range(1, count)];
        McpAbiParam array = new(string.Empty, McpAbiType.ArrayOf(McpAbiType.UInt256));
        Assert.That(McpAbiCodec.TryEncode([array, array], [JsonSerializer.SerializeToElement(ids), JsonSerializer.SerializeToElement(ids.Select(static _ => 10))],
            1 << 16, out byte[]? data, out string? error), Is.True, error);
        return new LogEntryForRpc
        {
            Address = TestItem.AddressC,
            Data = data!,
            Topics = [Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])"), Topic(TestItem.AddressA), Topic(TestItem.AddressA), Topic(TestItem.AddressB)]
        };

        static Hash256 Topic(Address address)
        {
            byte[] word = new byte[32];
            address.Bytes.CopyTo(word.AsSpan(12));
            return new Hash256(word);
        }
    }
}

/// <summary>Fee reporting on a London chain, where part of every fee is the burnt base fee.</summary>
[Parallelizable(ParallelScope.Self)]
public class McpTransactionToolsLondonTests
{
    private static readonly UInt256 GasPrice = 100_000_000_000;

    [Test]
    public async Task Explain_and_block_summary_split_base_fee_and_tip()
    {
        // London from block 1, so that block starts at the fork base fee instead of the genesis' zero.
        ReleaseSpec london = ((ReleaseSpec)London.Instance).Clone();
        london.Eip1559TransitionBlock = 1;
        await using McpTestNode node = await McpTestNode.Create(
            c => c.MaxConcurrentToolCalls = 16,
            b => b.AddSingleton<ISpecProvider>(new TestSpecProvider(london)));
        BasicTestBlockchain chain = node.Chain;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction transfer = Build.A.Transaction.WithChainId(chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(GasPrice)
            .WithTo(TestItem.AddressC).WithValue(1).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyB).TestObject;
        Block block = await chain.AddBlock(transfer);
        Assert.That(block.Transactions, Has.Length.EqualTo(1), "precondition: the transfer must be mined");
        UInt256 baseFee = block.BaseFeePerGas;
        Assert.That(baseFee, Is.GreaterThan(UInt256.Zero), "precondition: London blocks have a base fee");
        await using McpClient client = await node.CreateClient();

        JsonElement fees = McpAssert.Success(await McpToolCalls.Call(client, "explain_transaction", [("hash", transfer.Hash!.ToString())])).GetProperty("fees");
        JsonElement summary = McpAssert.Success(await McpToolCalls.Call(client, "block_summary", [("block", "latest")]));

        UInt256 burnt = baseFee * GasCostOf.Transaction;
        UInt256 total = GasPrice * GasCostOf.Transaction;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fees.GetProperty("baseFee").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(burnt)));
            Assert.That(fees.GetProperty("baseFee").GetProperty("destination").GetString(), Is.EqualTo("burnt"));
            Assert.That(fees.GetProperty("priorityFee").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(total - burnt)));
            Assert.That(fees.GetProperty("effectiveGasPriceGwei").GetString(), Is.EqualTo("100"));
            Assert.That(summary.GetProperty("baseFees").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(burnt)));
            Assert.That(summary.GetProperty("receipts").GetProperty("priorityFees").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(total - burnt)));
            Assert.That(summary.GetProperty("summary").GetString(), Does.Contain("burnt"));
        }
    }
}

/// <summary>
/// Deploys the test contracts and mines one block of calls to them from <see cref="TestItem.PrivateKeyB"/>:
/// a 1.5 ETH transfer, a call reverting with <c>Error(string)</c>, an out-of-gas loop, an ERC-20-shaped <c>Transfer</c>
/// event, a value forwarder and a self-recursive contract.
/// </summary>
internal sealed record McpTxScenario(
    Block Block,
    Transaction Transfer,
    Transaction RevertCall,
    Transaction OutOfGasCall,
    Transaction TokenCall,
    Transaction ForwardCall,
    Transaction RecursiveCall,
    Transaction RevertDeploy,
    Address RevertContract,
    Address TokenContract,
    Address Forwarder,
    Address Recursive)
{
    public const string RevertReason = "insufficient balance";
    public const ulong OutOfGasLimit = 60_000;
    public static readonly UInt256 TransferValue = 1_500_000_000_000_000_000;
    public static readonly UInt256 ForwardValue = 1000;
    public static readonly UInt256 TokenAmount = 1_250_000_000;
    public static readonly Hash256 TransferTopic = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");

    /// <summary>ABI-encoded <c>Error("insufficient balance")</c>.</summary>
    public static readonly byte[] RevertData = EncodeError(RevertReason);

    public static readonly byte[] RevertRuntimeCode = Prepare.EvmCode.StoreDataInMemory(0, RevertData).Revert(RevertData.Length, 0).Done;

    /// <summary><c>transfer(AddressD, TokenAmount)</c> call data; the token contract ignores it and always logs the same event.</summary>
    public static readonly byte[] TransferCallData = Bytes.Concat(
        Bytes.FromHexString("0xa9059cbb"),
        TestItem.AddressD.Bytes.PadLeft(32),
        TokenAmount.ToBigEndian());

    public static async Task<McpTxScenario> Create(McpTestNode node)
    {
        BasicTestBlockchain chain = node.Chain;
        PrivateKey sender = TestItem.PrivateKeyB;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, sender.Address);

        byte[] tokenRuntime = Prepare.EvmCode
            .StoreDataInMemory(0, TokenAmount.ToBigEndian())
            // LOGn pops the last pushed topic first, so this emits [Transfer, from, to].
            .Log(32, 0, [TestItem.AddressD.ToHash().ToHash256(), sender.Address.ToHash().ToHash256(), TransferTopic])
            .Op(Instruction.STOP)
            .Done;
        byte[] loopRuntime = Bytes.FromHexString("0x5b600056"); // JUMPDEST PUSH1 0 JUMP
        byte[] forwarderRuntime = Prepare.EvmCode.CallWithValue(TestItem.AddressC, 50_000).Op(Instruction.STOP).Done;
        byte[] recursiveRuntime = Prepare.EvmCode
            .PushData(0).PushData(0).PushData(0).PushData(0).PushData(0)
            .Op(Instruction.ADDRESS).Op(Instruction.GAS).Op(Instruction.CALL)
            .Op(Instruction.STOP)
            .Done;

        Transaction revertDeploy = Deploy(nonce, RevertRuntimeCode);
        Transaction[] deployments =
        [
            revertDeploy,
            Deploy(nonce + 1, loopRuntime),
            Deploy(nonce + 2, tokenRuntime),
            Deploy(nonce + 3, forwarderRuntime),
            Deploy(nonce + 4, recursiveRuntime)
        ];
        Block deployBlock = await chain.AddBlock(deployments);
        Assert.That(deployBlock.Transactions, Has.Length.EqualTo(deployments.Length), "precondition: every deployment must be mined");

        Address revert = ContractAddress.From(sender.Address, nonce);
        Address loop = ContractAddress.From(sender.Address, nonce + 1);
        Address token = ContractAddress.From(sender.Address, nonce + 2);
        Address forwarder = ContractAddress.From(sender.Address, nonce + 3);
        Address recursive = ContractAddress.From(sender.Address, nonce + 4);

        ulong n = nonce + 5;
        Transaction transfer = Sign(Tx(n++).WithTo(TestItem.AddressC).WithValue(TransferValue).WithGasLimit(GasCostOf.Transaction));
        Transaction revertCall = Sign(Tx(n++).WithTo(revert).WithData(TransferCallData).WithGasLimit(100_000));
        Transaction outOfGas = Sign(Tx(n++).WithTo(loop).WithGasLimit(OutOfGasLimit));
        Transaction tokenCall = Sign(Tx(n++).WithTo(token).WithData(TransferCallData).WithGasLimit(100_000));
        Transaction forwardCall = Sign(Tx(n++).WithTo(forwarder).WithValue(ForwardValue).WithGasLimit(100_000));
        Transaction recursiveCall = Sign(Tx(n++).WithTo(recursive).WithGasLimit(1_000_000));

        Transaction[] calls = [transfer, revertCall, outOfGas, tokenCall, forwardCall, recursiveCall];
        Block block = await chain.AddBlock(calls);
        Assert.That(block.Transactions, Has.Length.EqualTo(calls.Length), "precondition: every call must be mined");

        return new McpTxScenario(block, transfer, revertCall, outOfGas, tokenCall, forwardCall, recursiveCall, revertDeploy,
            revert, token, forwarder, recursive);

        TransactionBuilder<Transaction> Tx(ulong txNonce) =>
            Build.A.Transaction.WithChainId(chain.SpecProvider.ChainId).WithNonce(txNonce).WithGasPrice(1);

        Transaction Sign(TransactionBuilder<Transaction> builder) => builder.SignedAndResolved(chain.EthereumEcdsa, sender).TestObject;

        Transaction Deploy(ulong txNonce, byte[] runtime) =>
            Sign(Tx(txNonce).WithCode(Prepare.EvmCode.ForInitOf(runtime).Done).WithGasLimit(300_000));
    }

    private static byte[] EncodeError(string reason)
    {
        byte[] text = Encoding.UTF8.GetBytes(reason);
        int padded = (text.Length + 31) / 32 * 32;
        byte[] data = new byte[4 + 32 + 32 + padded];
        Bytes.FromHexString("0x08c379a0").CopyTo(data, 0);
        data[4 + 31] = 0x20;
        ((UInt256)(ulong)text.Length).ToBigEndian().CopyTo(data, 4 + 32);
        text.CopyTo(data, 4 + 64);
        return data;
    }
}
