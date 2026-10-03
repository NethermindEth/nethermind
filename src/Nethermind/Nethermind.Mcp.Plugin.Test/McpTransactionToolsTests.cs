// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.TxPool;
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
    public void Summary_uses_sender_net_deltas_for_multi_token_outputs()
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
        StringBuilder summary = new("; " + McpTxFormat.Short(bot) + " ");

        tools.AppendSenderSwap(summary, bot, movements, tokens);

        string text = summary.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.StartWith("; "));
            Assert.That(text, Does.Contain("swapped 33,522.6963 USDC for 12.45 WETH and 5 USDT (unverified token"));
        }
    }

    [Test]
    public void Summary_does_not_assign_another_recipients_swap_to_the_sender([Values] bool throughRouter)
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        Address sender = TestItem.AddressA;
        Address router = TestItem.AddressB;
        Address pool = TestItem.AddressC;
        Address recipient = TestItem.AddressD;
        Address sold = new("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48");
        Address bought = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2");
        List<McpTokenMovement> movements = [new(sold, "ERC-20", sender, throughRouter ? router : pool, 10_000_000, null)];
        if (throughRouter) movements.Add(new(sold, "ERC-20", router, pool, 10_000_000, null));
        movements.Add(new(bought, "ERC-20", pool, recipient, UInt256.Parse("3000000000000000000"), null));
        Dictionary<AddressAsKey, McpTokenInfo> tokens = new()
        {
            [sold] = new McpTokenInfo(sold, "USD Coin", "USDC", 6),
            [bought] = new McpTokenInfo(bought, "Wrapped Ether", "WETH", 18),
        };
        StringBuilder summary = new();

        bool described = tools.AppendSenderSwap(summary, sender, movements, tokens);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False);
            Assert.That(summary.ToString(), Is.Empty);
        }
    }

    private static readonly Address[] SwapTokens =
    [
        new("0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"),
        new("0x6B175474E89094C44Da98b954EedeAC495271d0F"),
        new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2"),
        new("0x6810e776880c02933d47db1b9fc05908e5386b96"),
    ];

    private static Address SwapAccount(int id) => new("0x" + id.ToString("x40"));

    private static McpTokenMovement SwapMove(int token, int from, int to, ulong amount, bool unwrap = false) =>
        new(SwapTokens[token], unwrap ? "WETH" : "ERC-20", SwapAccount(from), SwapAccount(to),
            UInt256.Parse("1000000000000000000") * amount, null);

    private static IEnumerable<TestCaseData> TerminalSwapCases()
    {
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            SwapMove(0, 1, 2, 100), SwapMove(1, 2, 3, 90), SwapMove(2, 3, 4, 80), SwapMove(2, 4, 0, 80, true)
        }).SetName("Swap_terminal_unwrap_excludes_pair_intermediates");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            SwapMove(0, 1, 2, 100), SwapMove(2, 2, 3, 90), SwapMove(1, 3, 5, 80)
        }).SetName("Swap_terminal_two_hop_names_Bob");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            SwapMove(0, 1, 2, 100), SwapMove(2, 2, 3, 90), SwapMove(3, 3, 4, 85), SwapMove(1, 4, 5, 80)
        }).SetName("Swap_terminal_three_hop_names_Bob");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            SwapMove(0, 1, 2, 100), SwapMove(1, 2, 4, 100), SwapMove(1, 4, 6, 1),
            SwapMove(1, 4, 5, 60), SwapMove(1, 4, 5, 39)
        }).SetName("Swap_terminal_groups_split_output_and_ignores_small_fee");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            SwapMove(0, 1, 2, 99), SwapMove(0, 1, 6, 1), SwapMove(1, 2, 6, 1000), SwapMove(1, 2, 5, 80)
        }).SetName("Swap_terminal_excludes_input_fee_counterparty");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            SwapMove(0, 1, 2, 100), SwapMove(2, 1, 2, 10), SwapMove(3, 1, 2, 3), SwapMove(1, 2, 5, 80)
        }).SetName("Swap_terminal_reports_omitted_inputs");
    }

    [TestCaseSource(nameof(TerminalSwapCases))]
    public void Swap_summary_does_not_classify_third_party_terminal_receipts(object input)
    {
        List<McpTokenMovement> movements = (List<McpTokenMovement>)input;
        Dictionary<AddressAsKey, McpTokenInfo> metadata = SwapMetadata();
        StringBuilder summary = new();
        bool described = _node.Chain.Container.Resolve<McpTransactionTools>()
            .AppendSenderSwap(summary, SwapAccount(1), movements, metadata);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False);
            Assert.That(summary.ToString(), Is.Empty);
        }
    }

    private static readonly Address Wbtc = new("0x2260FAC5E5542a773Aa44fBCfeDf7C193bc2C599");

    private static McpTokenMovement MixedSwapMove(Address token, int from, int to, string amount, bool unwrap = false) =>
        new(token, unwrap ? "WETH" : "ERC-20", SwapAccount(from), SwapAccount(to), UInt256.Parse(amount), null);

    private static IEnumerable<TestCaseData> CallbackSwapCases()
    {
        Address usdc = SwapTokens[0], weth = SwapTokens[2];
        McpTokenMovement payment = MixedSwapMove(usdc, 1, 2, "3000000000");
        McpTokenMovement output = MixedSwapMove(Wbtc, 3, 5, "5000000");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            MixedSwapMove(weth, 2, 4, "1000000000000000000"), payment, output,
            MixedSwapMove(weth, 4, 3, "1000000000000000000")
        }).SetName("Swap_V3_exact_in_names_the_terminal_output");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            MixedSwapMove(Wbtc, 2, 4, "100000000"), payment,
            MixedSwapMove(weth, 3, 6, "50000000000000000"), MixedSwapMove(Wbtc, 4, 3, "100000000"),
            MixedSwapMove(weth, 6, 0, "50000000000000000", true)
        }).SetName("Swap_V3_exact_in_unwraps_without_naming_a_pool");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            output, MixedSwapMove(weth, 2, 3, "1000000000000000000"), payment
        }).SetName("Swap_V3_exact_output_names_the_terminal_output");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            MixedSwapMove(usdc, 3, 1, "2900000000"),
            MixedSwapMove(weth, 2, 3, "1000000000000000000"), payment
        }).SetName("Swap_V3_losing_arbitrage_does_not_invent_an_output");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            MixedSwapMove(Wbtc, 2, 5, "5000000"), payment
        }).SetName("Swap_V3_single_hop_keeps_its_output");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            payment, MixedSwapMove(weth, 2, 4, "1000000000000000000"), output,
            MixedSwapMove(weth, 4, 3, "1000000000000000000")
        }).SetName("Swap_mixed_V2_V3_does_not_name_the_callback_payment");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            MixedSwapMove(SwapTokens[3], 1, 2, "1000000000000000000"),
            MixedSwapMove(usdc, 2, 5, "12000000"), MixedSwapMove(weth, 2, 5, "50000000000000000")
        }).SetName("Swap_LP_outputs_name_both_tokens_without_comparing_units");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            payment, MixedSwapMove(Wbtc, 2, 5, "5000000"), MixedSwapMove(SwapAccount(99), 2, 5, "12345")
        }).SetName("Swap_unknown_decimals_do_not_outweigh_a_known_token");
        yield return new TestCaseData(new List<McpTokenMovement>
        {
            payment, MixedSwapMove(Wbtc, 2, 5, "5000000"), MixedSwapMove(weth, 2, 5, "1000000000000000000"),
            MixedSwapMove(SwapTokens[3], 2, 5, "2000000000000000000")
        }).SetName("Swap_terminal_outputs_count_tokens_beyond_two");
    }

    [TestCaseSource(nameof(CallbackSwapCases))]
    public void Swap_summary_never_assigns_callback_outputs_to_the_sender(object input)
    {
        Dictionary<AddressAsKey, McpTokenInfo> metadata = SwapMetadata();
        metadata[SwapTokens[0]] = new(SwapTokens[0], "USD Coin", "USDC", 6);
        metadata[Wbtc] = new(Wbtc, "Wrapped Bitcoin", "WBTC", 8);
        StringBuilder summary = new();

        bool described = _node.Chain.Container.Resolve<McpTransactionTools>()
            .AppendSenderSwap(summary, SwapAccount(1), (List<McpTokenMovement>)input, metadata);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False);
            Assert.That(summary.ToString(), Is.Empty);
        }
    }

    private static Dictionary<AddressAsKey, McpTokenInfo> SwapMetadata() => new()
    {
        [SwapTokens[0]] = new(SwapTokens[0], "USD Coin", "USDC", 18),
        [SwapTokens[1]] = new(SwapTokens[1], "Dai", "DAI", 18),
        [SwapTokens[2]] = new(SwapTokens[2], "Wrapped Ether", "WETH", 18),
        [SwapTokens[3]] = new(SwapTokens[3], "Gnosis", "GNO", 18),
    };

    [Test]
    public void Plain_transfer_airdrop_and_mint_are_not_described_as_swaps([Values(1, 2, 3)] int kind)
    {
        List<McpTokenMovement> movements = kind switch
        {
            1 => [SwapMove(0, 1, 2, 100)],
            2 => [SwapMove(0, 1, 2, 50), SwapMove(0, 1, 3, 50)],
            _ => [SwapMove(0, 0, 1, 100)]
        };
        StringBuilder summary = new("prefix ");
        bool described = _node.Chain.Container.Resolve<McpTransactionTools>()
            .AppendSenderSwap(summary, SwapAccount(1), movements, SwapMetadata());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False);
            Assert.That(summary.ToString(), Is.EqualTo("prefix "));
        }
    }

    [Test]
    public void Swap_summary_never_selects_a_non_sender_after_five_thousand_hops()
    {
        List<McpTokenMovement> movements = [];
        for (int i = 5000; i >= 1; i--)
            movements.Add(MixedSwapMove(SwapAccount(10000 + i), i, i + 1, "100"));
        movements.Add(MixedSwapMove(SwapTokens[1], 5001, 5002, "80000000000000000000"));
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        Dictionary<AddressAsKey, McpTokenInfo> metadata = SwapMetadata();
        StringBuilder summary = new();
        bool described = tools.AppendSenderSwap(summary, SwapAccount(1), movements, metadata);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False, "a long route cannot make a third party the sender's output");
            Assert.That(summary.ToString(), Does.Not.Contain("swapped"));
        }
    }

    [Test]
    public async Task Tax_token_swap_back_is_not_the_senders_swap([Values] bool unwrap, [Values] bool feeFromSender)
    {
        Address tax = SwapAccount(99);
        List<McpTokenMovement> movements =
        [
            MixedSwapMove(tax, 1, 5, "100000000000000000000"),
            MixedSwapMove(tax, feeFromSender ? 1 : 5, 99, "1000000000000000000"),
            MixedSwapMove(tax, 99, 2, "1000000000000000000")
        ];
        if (unwrap)
        {
            movements.Add(MixedSwapMove(SwapTokens[2], 2, 4, "200000000000000000"));
            movements.Add(MixedSwapMove(SwapTokens[2], 4, 0, "200000000000000000", true));
        }
        else movements.Add(MixedSwapMove(SwapTokens[0], 2, 6, "500000000"));

        string summary = await ExplainMovementSummary(movements);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary, Does.Contain($"sent {(feeFromSender ? 101 : 100)} TAX"));
            Assert.That(summary, Does.Not.Contain("swapped"));
        }
    }

    [Test]
    public async Task Swap_unproven_router_unwrap_does_not_claim_a_native_recipient()
    {
        List<McpTokenMovement> movements =
        [
            MixedSwapMove(SwapTokens[2], 2, 4, "1000000000000000000"),
            MixedSwapMove(SwapTokens[0], 1, 2, "3000000000"),
            MixedSwapMove(SwapTokens[2], 4, 6, "2500000000000000"),
            MixedSwapMove(SwapTokens[2], 4, 0, "997500000000000000", true)
        ];

        string summary = await ExplainMovementSummary(movements);

        Assert.That(summary, Does.Contain("sent 3,000 USDC").And.Not.Contain("swapped").And.Not.Contain("0.9975 ETH"));
    }

    [Test]
    public void Swap_input_counterparty_is_not_an_unwrapped_output()
    {
        List<McpTokenMovement> movements =
        [
            SwapMove(0, 1, 4, 100), SwapMove(0, 1, 2, 100),
            SwapMove(2, 2, 4, 1), SwapMove(2, 4, 0, 1, true)
        ];
        StringBuilder summary = new("prefix ");

        bool described = _node.Chain.Container.Resolve<McpTransactionTools>()
            .AppendSenderSwap(summary, SwapAccount(1), movements, SwapMetadata());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Is.False);
            Assert.That(summary.ToString(), Is.EqualTo("prefix "));
        }
    }

    [Test]
    public async Task Swap_paid_to_an_NFT_seller_preserves_the_item_received()
    {
        List<McpTokenMovement> movements =
        [
            MixedSwapMove(SwapTokens[0], 1, 2, "100000000"),
            MixedSwapMove(SwapTokens[2], 2, 5, "40000000000000000"),
            new(SwapAccount(98), "ERC-721", SwapAccount(5), SwapAccount(1), 1, 42)
        ];

        string summary = await ExplainMovementSummary(movements);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary, Does.Contain("100 USDC"));
            Assert.That(summary, Does.Contain("0.04 WETH"));
            Assert.That(summary, Does.Contain("ERC-721").And.Contain("#42"));
        }
    }

    [Test]
    public async Task Native_input_swap_names_another_recipient_and_preserves_self_wording([Values] bool toSender)
    {
        List<McpTokenMovement> movements =
        [
            new(SwapTokens[2], "WETH", Address.Zero, SwapAccount(4), UInt256.Parse("1000000000000000000"), null),
            MixedSwapMove(SwapTokens[2], 4, 2, "1000000000000000000"),
            MixedSwapMove(SwapTokens[0], 2, toSender ? 1 : 5, "3000000000")
        ];

        string summary = await ExplainMovementSummary(movements, UInt256.Parse("1000000000000000000"));

        if (toSender) Assert.That(summary, Does.Contain("sent 1 ETH to").And.Contain("received 3,000 USDC"));
        else Assert.That(summary, Does.Contain("sent 1 ETH").And.Contain("3,000 USDC delivered to " + McpTxFormat.Short(SwapAccount(5))).And.Not.Contain("swapped"));
    }

    private static IEnumerable<TestCaseData> SenderSummaryCases()
    {
        Address tax = SwapAccount(99), usdc = SwapTokens[0], weth = SwapTokens[2], pair = SwapAccount(2);
        McpTokenMovement payment = MixedSwapMove(usdc, 1, 2, "3000000000");
        yield return Case("Sender_summary_tax_sell_uses_only_its_net_native_receipt",
            [MixedSwapMove(tax, 1, 2, "100000000000000000000"), MixedSwapMove(tax, 99, 2, "1000000000000000000"),
                MixedSwapMove(weth, 2, 4, "500000000000000000"), MixedSwapMove(weth, 4, 0, "500000000000000000", true),
                MixedSwapMove(weth, 2, 4, "20000000000000000"), MixedSwapMove(weth, 4, 0, "20000000000000000", true)],
            [Native(4, 6, "500000000000000000"), Native(4, 1, "20000000000000000")], "0",
            ["100 TAX", "for 0.02 ETH"], ["0.5 ETH"]);
        yield return Case("Sender_summary_handler_fee_does_not_create_a_swap",
            [MixedSwapMove(tax, 1, 5, "99000000000000000000"), MixedSwapMove(tax, 1, 7, "1000000000000000000"),
                MixedSwapMove(tax, 7, 2, "1000000000000000000"), MixedSwapMove(weth, 2, 4, "500000000000000000"),
                MixedSwapMove(weth, 4, 0, "500000000000000000", true)],
            [Native(4, 6, "500000000000000000")], "0", ["sent 100 TAX"], ["swapped", "sold"]);
        yield return Case("Sender_summary_keeps_WETH_and_separate_native_receipts",
            [payment, MixedSwapMove(weth, 2, 1, "1000000000000000000"), MixedSwapMove(weth, 2, 4, "100000000000000000"),
                MixedSwapMove(weth, 4, 0, "100000000000000000", true)],
            [Native(4, 1, "100000000000000000")], "0", ["for 1 WETH and 0.1 ETH"], ["unwrapped"]);
        yield return Case("Sender_summary_unwrap_counts_native_once",
            [payment, MixedSwapMove(weth, 2, 1, "1000000000000000000"), MixedSwapMove(weth, 1, 0, "1000000000000000000", true)],
            [new McpValueTransfer("CALL", weth, SwapAccount(1), UInt256.Parse("1000000000000000000"), 1)], "0",
            ["for 1 ETH"], ["WETH", "received 1 ETH"]);
        yield return Case("Sender_summary_LP_burn_and_deliveries_are_factual",
            [MixedSwapMove(pair, 1, 2, "1000000000000000000"), MixedSwapMove(pair, 2, 0, "1000000000000000000"),
                MixedSwapMove(usdc, 2, 5, "10000000"), MixedSwapMove(weth, 2, 5, "4000000000000000")],
            [], "0", ["removed liquidity: burned 1 LP", "10 USDC", "0.004 WETH", "delivered to " + McpTxFormat.Short(SwapAccount(5))], ["swapped"]);
        yield return Case("Sender_summary_third_party_output_is_a_delivery",
            [payment, MixedSwapMove(Wbtc, 2, 5, "5000000")], [], "0",
            ["sent 3,000 USDC", "0.05 WBTC delivered to " + McpTxFormat.Short(SwapAccount(5))], ["swapped", "sold"]);
        yield return Case("Sender_summary_NFT_purchase_names_item_collection_and_payment",
            [new McpTokenMovement(SwapAccount(98), "ERC-721", SwapAccount(5), SwapAccount(1), 1, 42)], [], "2000000000000000000",
            ["bought ERC-721 #42 (Collection " + McpTxFormat.Short(SwapAccount(98)) + ") for 2 ETH"], ["swapped"]);
        yield return Case("Sender_summary_NFT_for_another_receiver_is_factual",
            [new McpTokenMovement(SwapAccount(98), "ERC-721", SwapAccount(5), SwapAccount(6), 1, 42)], [], "2000000000000000000",
            ["sent 2 ETH", "ERC-721", "#42", "delivered to " + McpTxFormat.Short(SwapAccount(6))], ["bought", "swapped"]);
        yield return Case("Sender_summary_uses_net_input_after_refunds",
            [payment, MixedSwapMove(usdc, 2, 1, "600000000"), MixedSwapMove(weth, 2, 1, "1000000000000000000")], [], "0",
            ["swapped 2,400 USDC for 1 WETH"], ["3,000 USDC"]);

        yield return Case("Sender_summary_keeps_NFT_sent_when_receiving_payment",
            [new McpTokenMovement(SwapAccount(98), "ERC-721", SwapAccount(1), SwapAccount(5), 1, 42),
                MixedSwapMove(usdc, 5, 1, "10000000")], [], "0",
            ["sent ERC-721", "#42", "received 10 USDC"], ["swapped", "bought"]);

        static TestCaseData Case(string name, List<McpTokenMovement> movements, List<McpValueTransfer> transfers, string value,
            string[] includes, string[] excludes) => new TestCaseData(movements, transfers, value, includes, excludes).SetName(name);
        static McpValueTransfer Native(int from, int to, string value) => new("CALL", SwapAccount(from), SwapAccount(to), UInt256.Parse(value), 1);
    }

    [TestCaseSource(nameof(SenderSummaryCases))]
    public async Task Sender_summary_requires_its_own_net_outputs(object movements, object transfers, string value, string[] includes, string[] excludes)
    {
        string summary = await ExplainMovementSummary((List<McpTokenMovement>)movements, UInt256.Parse(value), (List<McpValueTransfer>)transfers);
        using (Assert.EnterMultipleScope())
        {
            foreach (string expected in includes) Assert.That(summary, Does.Contain(expected));
            foreach (string unexpected in excludes) Assert.That(summary, Does.Not.Contain(unexpected));
        }
    }

    [Test]
    public async Task Unknown_receipt_status_does_not_claim_transaction_effects()
    {
        string summary = await ExplainMovementSummary(
            [MixedSwapMove(SwapTokens[0], 1, 2, "1000000"), MixedSwapMove(SwapTokens[1], 2, 1, "1000000000000000000")],
            status: null);

        Assert.That(summary, Does.Not.Contain("swapped").And.Not.Contain("sent").And.Not.Contain("received"));
    }

    [Test]
    public async Task Explain_ignores_movements_beyond_the_reported_transfer_limit_when_summarizing()
    {
        List<McpTokenMovement> movements = [];
        for (int i = 0; i < McpTransactionTools.MaxTokenTransfers; i++)
            movements.Add(MixedSwapMove(SwapTokens[0], 2, 3, "1"));
        movements.Add(MixedSwapMove(SwapTokens[1], 2, 1, "1000000000000000000"));

        string summary = await ExplainMovementSummary(movements);

        Assert.That(summary, Does.Not.Contain("received"));
    }

    private static async Task<string> ExplainMovementSummary(List<McpTokenMovement> movements, UInt256 value = default,
        List<McpValueTransfer>? nativeTransfers = null, byte? status = 1)
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder => builder
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        Block block = (await node.Seed()).Block;
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getHeaderByNumber(Arg.Any<BlockParameter>()).Returns(ResultWrapper<BlockHeaderForRpc?>.Success(null));
        eth.eth_getTransactionByHash(Arg.Any<Hash256>()).Returns(ResultWrapper<TransactionForRpc?>.Success(
            new LegacyTransactionForRpc
            {
                Hash = TestItem.KeccakA,
                BlockNumber = block.Number,
                BlockHash = block.Hash,
                TransactionIndex = 0,
                From = SwapAccount(1),
                To = SwapAccount(4),
                Nonce = 0,
                Value = value,
                Gas = 100_000,
                GasPrice = 1,
                Input = []
            }));
        eth.eth_getTransactionReceipt(Arg.Any<Hash256>()).Returns(ResultWrapper<ReceiptForRpc?>.Success(
            new ReceiptForRpc { Status = status, GasUsed = 50_000, EffectiveGasPrice = 1, Logs = movements.Select(MovementLog).ToArray() }));
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(ResultWrapper<byte[]>.Success([0x60]));
        eth.eth_call(Arg.Any<SignableTransactionForRpc>(), Arg.Any<BlockParameter?>(), Arg.Any<Dictionary<Address, AccountOverride>?>(), Arg.Any<BlockOverride?>())
            .Returns(info =>
            {
                LegacyTransactionForRpc call = (LegacyTransactionForRpc)info.ArgAt<SignableTransactionForRpc>(0);
                byte[] output;
                if (call.Input![0] == 0x31)
                {
                    output = new byte[32];
                    output[^1] = call.To == SwapTokens[0] ? (byte)6 : call.To == Wbtc ? (byte)8 : (byte)18;
                }
                else output = TestContracts.AbiString(call.To == SwapTokens[0] ? "USDC" : call.To == SwapTokens[2] ? "WETH" : call.To == Wbtc ? "WBTC" : call.To == SwapAccount(2) ? "LP" : "TAX");
                return ResultWrapper<HexBytes>.Success(new HexBytes(output));
            });
        FaultInjectingRpcModuleProvider provider = (FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>();
        provider.Override(nameof(IEthRpcModule.eth_getTransactionByHash), eth);
        provider.Override(nameof(IEthRpcModule.eth_call), eth);
        if (nativeTransfers is not null)
        {
            IDebugRpcModule debug = Substitute.For<IDebugRpcModule>();
            debug.debug_traceTransaction(Arg.Any<Hash256>(), Arg.Any<GethTraceOptions>()).Returns(_ =>
            {
                NativeCallTracerCallFrame root = new() { Type = Instruction.CALL, From = SwapAccount(1), To = SwapAccount(4), Value = value };
                foreach (McpValueTransfer transfer in nativeTransfers)
                    root.Calls.Add(new NativeCallTracerCallFrame { Type = Instruction.CALL, From = transfer.From, To = transfer.To, Value = transfer.Value });
                return ResultWrapper<GethLikeTxTrace>.Success(new GethLikeTxTrace { CustomTracerResult = new GethLikeCustomTrace { Value = root } });
            });
            provider.Override(nameof(IDebugRpcModule.debug_traceTransaction), debug);
        }
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "explain_transaction", [("hash", TestItem.KeccakA.ToString())]);
        JsonElement result = McpAssert.Success(call);
        await McpToolCalls.AssertConformsToOutputSchema(client, "explain_transaction", call);
        return result.GetProperty("summary").GetString()!;
    }

    private static LogEntryForRpc MovementLog(McpTokenMovement movement)
    {
        byte[] data = new byte[32];
        movement.Amount.ToBigEndian(data);
        Hash256[] topics;
        if (movement.Standard == "WETH")
        {
            bool deposit = movement.From == Address.Zero;
            topics = [Keccak.Compute(deposit ? "Deposit(address,uint256)" : "Withdrawal(address,uint256)"),
                (deposit ? movement.To : movement.From).ToHash().ToHash256()];
        }
        else
        {
            topics = [McpKnownAbi.TransferTopic, movement.From.ToHash().ToHash256(), movement.To.ToHash().ToHash256()];
            if (movement.TokenId is { } id)
            {
                id.ToBigEndian(data);
                topics = [.. topics, new Hash256(data)];
                data = [];
            }
        }
        return new LogEntryForRpc { Address = movement.Token, Data = data, Topics = topics };
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

        (Dictionary<AddressAsKey, McpTokenInfo> found, int skipped) = McpTxTokens.LookUp(new McpTokenMetadata(), eth, tokens,
            BlockParameter.Latest, 10, CancellationToken.None, static () => true);

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
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(receipts));
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
    public void Block_receipt_stats_observe_cancellation_while_scanning_receipts()
    {
        McpTransactionTools tools = _node.Chain.Container.Resolve<McpTransactionTools>();
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getBlockReceipts(Arg.Any<BlockParameter>()).Returns(ResultWrapper<IEnumerable<ReceiptForRpc>?>.Success(
            [new ReceiptForRpc { Status = 1, Logs = [] }]));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.That(() => tools.ReceiptStats(eth, new BlockParameter(1), null, [], static () => false, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
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

        JsonElement result = await Success("explain_transaction", ("hash", pending.Hash!.ToString()), ("includeUsd", true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("pending"));
            Assert.That(result.TryGetProperty("block", out _), Is.False);
            Assert.That(result.GetProperty("fees").GetProperty("maxFee").GetProperty("wei").GetString(), Is.EqualTo(McpAssert.Hex(2 * GasCostOf.Transaction)));
            Assert.That(result.GetProperty("summary").GetString(), Does.StartWith("Pending in the mempool"));
            Assert.That(result.GetProperty("notes").GetRawText(), Does.Contain("USD omitted").And.Contain("pending"));
        }
    }

    [Test]
    public async Task Explain_usd_omission_is_explicit_when_receipts_are_pruned()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.EnableTracing = false, builder => builder
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        McpTxScenario scenario = await McpTxScenario.Create(node);
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getTransactionByHash(Arg.Any<Hash256>()).Returns(ResultWrapper<TransactionForRpc?>.Success(new LegacyTransactionForRpc
        {
            Hash = scenario.Transfer.Hash,
            From = TestItem.AddressB,
            To = TestItem.AddressC,
            Nonce = 0,
            Gas = 21_000,
            GasPrice = 1,
            Value = 1,
            BlockNumber = scenario.Block.Number,
            BlockHash = scenario.Block.Hash,
            TransactionIndex = 0
        }));
        eth.eth_getHeaderByNumber(Arg.Any<BlockParameter>()).Returns(ResultWrapper<BlockHeaderForRpc?>.Success(null));
        eth.eth_getTransactionReceipt(Arg.Any<Hash256>()).Returns(ResultWrapper<ReceiptForRpc?>.Success(null));
        eth.eth_getCode(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(ResultWrapper<byte[]>.Success([]));
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).Override(nameof(IEthRpcModule.eth_getTransactionByHash), eth);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "explain_transaction", [("hash", scenario.Transfer.Hash!.ToString()), ("includeUsd", true)]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("notes").GetRawText(), Does.Contain("USD omitted").And.Contain("receipt"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "explain_transaction", call);
    }

    [Test]
    public async Task Diagnose_mined_transaction_precedes_pool_state()
    {
        CallToolResult call = await McpToolCalls.Call(_client, "diagnose_transaction", [("hash", _scenario.Transfer.Hash!.ToString())]);
        JsonElement result = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("mined"));
            Assert.That(result.GetProperty("blockNumber").GetUInt64(), Is.EqualTo(_scenario.Block.Number));
            Assert.That(result.GetProperty("canSend").GetBoolean(), Is.False);
        }

        await McpToolCalls.AssertConformsToOutputSchema(_client, "diagnose_transaction", call);
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(2, null)]
    [TestCase(null, null)]
    public async Task Diagnose_mined_receipt_outcomes_and_missing_index_conform(int? status, bool? succeeded)
    {
        Transaction tx = DiagnosisPoolTransaction(5);
        await using McpTestNode node = await DiagnosisPoolNode([], lookup: tx, configureEth: eth =>
        {
            eth.eth_getTransactionByHash(Arg.Any<Hash256>()).Returns(ResultWrapper<TransactionForRpc?>.Success(
                new LegacyTransactionForRpc { Hash = tx.Hash, BlockNumber = 1, Nonce = 5, From = TestItem.AddressB }));
            eth.eth_getTransactionReceipt(Arg.Any<Hash256>()).Returns(ResultWrapper<ReceiptForRpc?>.Success(
                status is null ? null : new ReceiptForRpc { Status = (byte)status.Value }));
        });
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", tx.Hash!.ToString())]);
        JsonElement result = McpAssert.Success(call);

        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("mined"));
        Assert.That(result.GetProperty("succeeded").Deserialize<bool?>(), Is.EqualTo(succeeded));
        Assert.That(result.TryGetProperty("transactionIndex", out _), Is.False);
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_pending_transaction_reads_the_pool_and_account_nonces()
    {
        await using McpTestNode node = await McpTestNode.Create();
        PrivateKey sender = TestItem.PrivateKeyC;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        Transaction pending = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(2)
            .WithTo(TestItem.AddressD).WithValue(7).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
        node.Chain.AddTransactions(pending);
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", pending.Hash!.ToString())]);
        JsonElement diagnosis = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis.GetProperty("status").GetString(), Is.EqualTo("pending_ready"));
            Assert.That(diagnosis.GetProperty("poolAvailable").GetBoolean(), Is.True);
            Assert.That(diagnosis.GetProperty("latestNonce").GetString(), Is.EqualTo(nonce.ToString()));
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_unknown_hash_explains_that_the_node_does_not_know_it()
    {
        CallToolResult call = await McpToolCalls.Call(_client, "diagnose_transaction", [("hash", UnknownHash)]);
        JsonElement result = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("not_found"));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain("not mined or known").And.Contain("sped up or cancelled")
                .And.Contain("diagnose_transaction with your address"));
        }

        await McpToolCalls.AssertConformsToOutputSchema(_client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_remains_available_when_the_txpool_RPC_module_is_disabled()
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: static builder => builder
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        ITxPoolRpcModule pool = Substitute.For<ITxPoolRpcModule>();
        pool.txpool_contentFrom(Arg.Any<Address>())
            .Returns(ResultWrapper<TxPoolContentFrom>.Fail("txpool module disabled", ErrorCodes.MethodNotFound));
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>())
            .Override(nameof(ITxPoolRpcModule.txpool_contentFrom), pool);
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("address", TestItem.AddressB.ToString())]);
        JsonElement result = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("address"));
            Assert.That(result.GetProperty("poolAvailable").GetBoolean(), Is.True);
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    private static Transaction DiagnosisPoolTransaction(ulong nonce, bool blob = true) => new()
    {
        Nonce = nonce,
        SenderAddress = TestItem.AddressB,
        To = TestItem.AddressC,
        Hash = Keccak.Compute($"diagnosis-{nonce}-{blob}"),
        Type = blob ? TxType.Blob : TxType.EIP1559,
        GasPrice = 2,
        DecodedMaxFeePerGas = 20,
        MaxFeePerBlobGas = 10,
        GasLimit = 21_000
    };

    private static async Task<McpTestNode> DiagnosisPoolNode(Transaction[] transactions, ulong latest = 5, Transaction? lookup = null, Action<IEthRpcModule>? configureEth = null)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        Transaction[] standard = transactions.Where(static tx => tx.Type != TxType.Blob).ToArray();
        Transaction[] blobs = transactions.Where(static tx => tx.Type == TxType.Blob).ToArray();
        pool.GetPendingTransactionsBySender(Arg.Any<Address>()).Returns(standard);
        pool.GetPendingLightBlobTransactionsBySender(Arg.Any<Address>()).Returns(blobs);
        pool.GetPendingTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]> { [TestItem.AddressB] = standard });
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]> { [TestItem.AddressB] = blobs });
        pool.GetPendingTransactions().Returns(standard);
        McpTestNode node = await McpTestNode.Create(configureContainer: builder => builder.AddSingleton(pool)
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        ulong pending = latest;
        while (transactions.Any(tx => tx.Nonce == pending)) pending++;
        eth.eth_getTransactionCount(Arg.Any<Address>(), Arg.Any<BlockParameter?>()).Returns(info =>
            Task.FromResult(ResultWrapper<UInt256>.Success(info.ArgAt<BlockParameter>(1).Type == BlockParameterType.Latest ? latest : pending)));
        eth.eth_feeHistory(Arg.Any<ulong>(), Arg.Any<BlockParameter>(), Arg.Any<double[]>())
            .Returns(_ => ResultWrapper<FeeHistoryResults>.Success(DiagnosisHistory()));
        eth.eth_baseFee().Returns(ResultWrapper<UInt256?>.Success(1));
        eth.eth_maxPriorityFeePerGas().Returns(ResultWrapper<UInt256?>.Success(1));
        eth.eth_blobBaseFee().Returns(ResultWrapper<UInt256?>.Success(1));
        TransactionForRpc? rpc = lookup is null ? null : new BlobTransactionForRpc
        {
            Hash = lookup.Hash,
            From = lookup.SenderAddress,
            Nonce = lookup.Nonce,
            MaxFeePerGas = lookup.MaxFeePerGas,
            MaxPriorityFeePerGas = lookup.MaxPriorityFeePerGas,
            MaxFeePerBlobGas = lookup.MaxFeePerBlobGas
        };
        eth.eth_getTransactionByHash(Arg.Any<Hash256>()).Returns(ResultWrapper<TransactionForRpc?>.Success(rpc));
        configureEth?.Invoke(eth);
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>())
            .Override(nameof(IEthRpcModule.eth_getTransactionByHash), eth);
        return node;
    }

    [Test]
    public async Task Diagnose_merges_blob_and_standard_sender_buckets([Values] bool mixed)
    {
        Transaction[] transactions = [DiagnosisPoolTransaction(5, !mixed), DiagnosisPoolTransaction(6), DiagnosisPoolTransaction(8)];
        await using McpTestNode node = await DiagnosisPoolNode(transactions);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("address", TestItem.AddressB.ToString())]);
        JsonElement result = McpAssert.Success(call);

        Assert.That(result.GetProperty("transactions").GetArrayLength(), Is.EqualTo(3), "both sender buckets must be listed");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("transactions")[2].GetProperty("status").GetString(), Is.EqualTo("pending_queued"));
            Assert.That(result.GetProperty("nonceGaps")[0].GetProperty("from").GetString(), Is.EqualTo("7"));
            Assert.That(result.GetProperty("nonceGaps")[0].GetProperty("to").GetString(), Is.EqualTo("7"));
        }
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_queued_blob_names_first_missing_merged_nonce()
    {
        Transaction queued = DiagnosisPoolTransaction(7);
        await using McpTestNode node = await DiagnosisPoolNode([DiagnosisPoolTransaction(5), queued], lookup: queued);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", queued.Hash!.ToString())]);
        Assert.That(McpAssert.Success(call).GetProperty("recommendations")[0].GetString(), Does.Contain("nonce 6 before nonce 7"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_caps_transactions_and_reports_gap_ranges()
    {
        Transaction[] transactions = Enumerable.Range(20, 60).Select(static n => DiagnosisPoolTransaction((ulong)n)).ToArray();
        await using McpTestNode node = await DiagnosisPoolNode(transactions);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("address", TestItem.AddressB.ToString())]);
        JsonElement result = McpAssert.Success(call);

        Assert.That(result.GetProperty("transactions").GetArrayLength(), Is.EqualTo(50));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("omitted").GetInt32(), Is.EqualTo(10));
            Assert.That(result.GetProperty("nonceGaps")[0].GetProperty("from").GetString(), Is.EqualTo("5"));
            Assert.That(result.GetProperty("nonceGaps")[0].GetProperty("to").GetString(), Is.EqualTo("19"));
            Assert.That(result.GetProperty("nonceGapsOmitted").GetInt32(), Is.Zero);
        }
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_later_nonce_is_blocked_by_an_underpriced_predecessor([Values] bool blob, [Values] bool byAddress)
    {
        Transaction blocker = DiagnosisPoolTransaction(5, blob);
        if (blob) blocker.MaxFeePerBlobGas = 1;
        else blocker.DecodedMaxFeePerGas = 5;
        Transaction later = DiagnosisPoolTransaction(6);
        await using McpTestNode node = await DiagnosisPoolNode([blocker, later], lookup: later);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction",
            [(byAddress ? "address" : "hash", byAddress ? TestItem.AddressB.ToString() : later.Hash!.ToString())]);
        JsonElement result = McpAssert.Success(call);
        JsonElement diagnosis = byAddress ? result.GetProperty("transactions")[1] : result;
        Assert.That(diagnosis.GetProperty("status").GetString(), Is.EqualTo("pending_blocked"));
        Assert.That(diagnosis.GetProperty("blockedBy").GetProperty("nonce").GetString(), Is.EqualTo("5"));
        Assert.That(diagnosis.GetProperty("blockedBy").GetProperty("hash").GetString(), Is.EqualTo(blocker.Hash!.ToString()));
        Assert.That(diagnosis.GetProperty("blockedBy").GetProperty("reason").GetString(), Is.EqualTo(blob ? "blob_underpriced" : "underpriced"));
        Assert.That(diagnosis.GetProperty("recommendations")[0].GetString(), Does.Contain("nonce 5").And.Contain("first"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_address_counts_stale_transactions_separately()
    {
        await using McpTestNode node = await DiagnosisPoolNode([DiagnosisPoolTransaction(4), DiagnosisPoolTransaction(5), DiagnosisPoolTransaction(7)]);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("address", TestItem.AddressB.ToString())]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.TryGetProperty("stale", out JsonElement stale), Is.True, "below-latest pool entries need their own count");
        Assert.That(stale.GetInt32(), Is.EqualTo(1));
        Assert.That(result.GetProperty("summary").GetString(), Does.Contain("1 pending and 1 queued").And.Contain("1 stale"));
        Assert.That(result.GetProperty("notes").GetRawText(), Does.Contain("stale"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_missing_nonce_must_be_submitted_not_replaced([Values] bool byAddress)
    {
        Transaction tx = DiagnosisPoolTransaction(7);
        await using McpTestNode node = await DiagnosisPoolNode([DiagnosisPoolTransaction(5), tx], lookup: tx);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction",
            [(byAddress ? "address" : "hash", byAddress ? TestItem.AddressB.ToString() : tx.Hash!.ToString())]);
        Assert.That(McpAssert.Success(call).GetProperty("recommendations")[0].GetString(),
            Does.StartWith("Submit the missing transaction at nonce 6").And.Not.Contain("replace"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public void Diagnose_legacy_advice_keeps_fixed_price_fields()
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(TxType.Legacy, 9, 9), 5, 6,
            new McpDiagnosisTools.FeeSnapshot(10, 4, 3, 5, 8));
        string advice = result["recommendations"]![0]!.GetValue<string>();
        Assert.That(advice, Does.Contain("type-0 replacement").And.Contain("gasPrice").And.Contain("25 wei").And.Not.Contain("maxPriorityFeePerGas"));
    }

    [Test]
    public void Diagnose_known_next_blob_fee_still_gets_headroom()
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(TxType.Blob, 20, 5, 3), 5, 6,
            new McpDiagnosisTools.FeeSnapshot(10, 4, 3, 5, 8));
        Assert.That(result["recommended"]!["maxFeePerBlobGasWei"]!.GetValue<string>(), Is.EqualTo("8"));
        Assert.That(result["replacementMinimum"]!["maxFeePerBlobGasWei"]!.GetValue<string>(), Is.EqualTo("6"));
        Assert.That(result["notes"]!.ToJsonString(), Does.Contain("twice the next block"));
    }

    [Test]
    public async Task Diagnose_blocked_transaction_retains_its_own_issues([Values("underpriced", "blob_underpriced", "nonce_gaps")] string issue, [Values] bool byAddress)
    {
        Transaction blocker = DiagnosisPoolTransaction(5, false);
        blocker.DecodedMaxFeePerGas = 5;
        Transaction later = DiagnosisPoolTransaction(issue == "nonce_gaps" ? 9UL : 6UL);
        if (issue == "underpriced") later.DecodedMaxFeePerGas = 5;
        if (issue == "blob_underpriced") later.MaxFeePerBlobGas = 1;
        Transaction[] pool = issue == "nonce_gaps" ? [blocker, DiagnosisPoolTransaction(7), later] : [blocker, later];
        await using McpTestNode node = await DiagnosisPoolNode(pool, lookup: later);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction",
            [(byAddress ? "address" : "hash", byAddress ? TestItem.AddressB.ToString() : later.Hash!.ToString())]);
        JsonElement root = McpAssert.Success(call);
        JsonElement result = byAddress ? root.GetProperty("transactions").EnumerateArray().Last() : root;
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("pending_blocked"));
        if (issue == "nonce_gaps")
        {
            Assert.That(result.TryGetProperty("nonceGaps", out JsonElement gaps), Is.True, "blocking fees must not hide missing nonces");
            Assert.That(gaps.EnumerateArray().Select(static gap => gap.GetProperty("from").GetString()), Is.EqualTo(new[] { "6", "8" }));
            Assert.That(result.GetProperty("recommendations").GetRawText(), Does.Contain("nonce 6"));
        }
        else
        {
            Assert.That(result.TryGetProperty("ownFeeStatus", out JsonElement feeStatus), Is.True, "blocked status must retain the transaction's own fee diagnosis");
            Assert.That(feeStatus.GetString(), Is.EqualTo(issue));
            Assert.That(result.GetProperty("summary").GetString(), Does.Contain(issue == "underpriced" ? "maxFeePerGas" : "maxFeePerBlobGas"));
            Assert.That(result.GetProperty("recommendations").GetRawText(), Does.Contain("Replace nonce 6"));
        }
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public void Diagnose_blocked_advice_labels_an_unknown_hash()
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(TxType.EIP1559, 20, 5), 4, 6,
            new(10, 4, 3, 5, 8), blockedBy: new(4, null, "underpriced"));
        Assert.That(result["recommendations"]![0]!.GetValue<string>(), Does.Contain("Unknown hash").And.Not.Contain("(, underpriced)"));
    }

    [Test]
    public void Diagnose_fixed_price_advice_uses_the_actual_type([Values(TxType.Legacy, TxType.AccessList)] TxType type)
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(type, 9, 9), 5, 6, new(10, 4, 3, 5, 8));
        string advice = result["recommendations"]![0]!.GetValue<string>();
        Assert.That(advice, Does.Contain($"type-{(int)type} replacement").And.Contain("gasPrice").And.Not.Contain("maxPriorityFeePerGas"));
    }

    [Test]
    public void Diagnose_large_effective_tip_does_not_suggest_raising_it([Values(TxType.Legacy, TxType.AccessList, TxType.EIP1559)] TxType type)
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(type, 1000, 500), 5, 6, new(10, 4, 3, 5, 8));
        Assert.That(result["lowTip"]!.GetValue<bool>(), Is.False);
        Assert.That(result["recommendations"]![0]!.GetValue<string>(), Does.Contain("fees are adequate").And.Not.Contain("Replace"));
    }

    [Test]
    public async Task Diagnose_summary_counts_only_the_listed_statuses([Values] bool blocked)
    {
        Transaction[] pool = Enumerable.Range(5, 60).Select(static nonce => DiagnosisPoolTransaction((ulong)nonce, false)).ToArray();
        if (blocked) pool[0].DecodedMaxFeePerGas = 5;
        await using McpTestNode node = await DiagnosisPoolNode(pool);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("address", TestItem.AddressB.ToString())]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("transactions").GetArrayLength(), Is.EqualTo(50));
        string summary = result.GetProperty("summary").GetString()!;
        Assert.That(summary, Does.Contain("showing 50 of 60").And.Contain("10 omitted"));
        Assert.That(summary, blocked ? Does.Contain("49 blocked").And.Contain("1 underpriced") : Does.Contain("50 pending"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    private static FeeHistoryResults DiagnosisHistory() => new(1,
        new ArrayPoolList<UInt256>(2, new UInt256[] { 8, 10 }), new ArrayPoolList<double>(1, new double[] { 0.5 }),
        new ArrayPoolList<UInt256>(2, new UInt256[] { 3, 4 }), new ArrayPoolList<double>(1, new double[] { 0.5 }),
        new ArrayPoolList<ArrayPoolList<UInt256>>(1, new[] { new ArrayPoolList<UInt256>(3, new UInt256[] { 3, 5, 8 }) }));

    private static LegacyTransactionForRpc DiagnosisFeeTransaction(TxType type, ulong cap, ulong tip, ulong blob = 3)
    {
        LegacyTransactionForRpc tx = type switch
        {
            TxType.Blob => new BlobTransactionForRpc { MaxFeePerBlobGas = blob },
            TxType.SetCode => new SetCodeTransactionForRpc(),
            TxType.EIP1559 => new EIP1559TransactionForRpc(),
            TxType.AccessList => new AccessListTransactionForRpc(),
            _ => new LegacyTransactionForRpc()
        };
        tx.Hash = TestItem.KeccakA;
        tx.From = TestItem.AddressB;
        tx.Nonce = 5;
        tx.GasPrice = cap;
        if (tx is EIP1559TransactionForRpc dynamic)
        {
            dynamic.MaxFeePerGas = cap;
            dynamic.MaxPriorityFeePerGas = tip;
        }
        return tx;
    }

    [Test]
    public void Diagnose_low_tip_is_ready_when_fee_cap_covers_next_base([Values(10UL, 11UL, 20UL)] ulong cap)
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(TxType.EIP1559, cap, 2), 5, 6, new McpDiagnosisTools.FeeSnapshot(10, 4, 3, 5, 8));
        Assert.That(result["status"]!.GetValue<string>(), Is.EqualTo("pending_ready"));
        Assert.That(result["lowTip"]!.GetValue<bool>(), Is.True);
    }

    [TestCase(TxType.Legacy, 9UL, 9UL, "10", null, "25")]
    [TestCase(TxType.AccessList, 9UL, 9UL, "10", null, "25")]
    [TestCase(TxType.EIP1559, 9UL, 0UL, "9", "1", "25")]
    [TestCase(TxType.EIP1559, 100UL, 10UL, "110", "11", "110")]
    [TestCase(TxType.SetCode, 9UL, 0UL, "9", "1", "25")]
    [TestCase(TxType.Blob, 9UL, 1UL, "18", "2", "25")]
    public async Task Diagnose_reports_exact_replacement_and_recommended_fees(
        TxType type, ulong cap, ulong tip, string minimum, string? minimumTip, string recommended)
    {
        JsonObject result = McpDiagnosisTools.Diagnose(DiagnosisFeeTransaction(type, cap, tip), 5, 6, new McpDiagnosisTools.FeeSnapshot(10, 4, 3, 5, 8));
        Assert.That(result.ContainsKey("replacementMinimum"), Is.True, "strict pool replacement minimum must be separate from the recommendation");
        string feeField = minimumTip is null ? "gasPriceWei" : "maxFeePerGasWei";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result["replacementMinimum"]![feeField]!.GetValue<string>(), Is.EqualTo(minimum));
            Assert.That(result["recommended"]![feeField]!.GetValue<string>(), Is.EqualTo(recommended));
            if (minimumTip is not null)
                Assert.That(result["replacementMinimum"]!["maxPriorityFeePerGasWei"]!.GetValue<string>(), Is.EqualTo(minimumTip));
            if (type == TxType.Blob)
                Assert.That(result["replacementMinimum"]!["maxFeePerBlobGasWei"]!.GetValue<string>(), Is.EqualTo("6"));
        }
        McpClientTool tool = (await _client.ListToolsAsync()).Single(static t => t.Name == "diagnose_transaction");
        McpSchemaValidator.AssertConforms(tool.ProtocolTool.OutputSchema!.Value,
            JsonSerializer.SerializeToElement(new JsonObject { ["result"] = result }));
    }

    [Test]
    public async Task Diagnose_missing_core_pool_is_explicit_and_schema_valid()
    {
        McpDiagnosisTools tool = new(_node.Chain.Container.Resolve<McpToolExecutor>(),
            _node.Chain.Container.Resolve<IMcpConfig>(), _node.Chain.Container.Resolve<McpNodeCapabilities>());
        CallToolResult call = await tool.DiagnoseTransaction(address: TestItem.AddressB.ToString());
        Assert.That(McpAssert.Success(call).GetProperty("status").GetString(), Is.EqualTo("txpool_unavailable"));
        await McpToolCalls.AssertConformsToOutputSchema(_client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_failed_fee_reads_never_claim_ready()
    {
        Transaction tx = DiagnosisPoolTransaction(5);
        await using McpTestNode node = await DiagnosisPoolNode([tx], lookup: tx, configureEth: eth =>
        {
            eth.eth_feeHistory(Arg.Any<ulong>(), Arg.Any<BlockParameter>(), Arg.Any<double[]>())
                .Returns(ResultWrapper<FeeHistoryResults>.Fail("unavailable", ErrorCodes.ResourceUnavailable));
            eth.eth_baseFee().Returns(ResultWrapper<UInt256?>.Fail("unavailable", ErrorCodes.ResourceUnavailable));
            eth.eth_blobBaseFee().Returns(ResultWrapper<UInt256?>.Fail("unavailable", ErrorCodes.ResourceUnavailable));
            eth.eth_maxPriorityFeePerGas().Returns(ResultWrapper<UInt256?>.Fail("unavailable", ErrorCodes.ResourceUnavailable));
        });
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", tx.Hash!.ToString())]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("fee_unavailable"));
        Assert.That(result.GetProperty("fees").GetProperty("nextBaseFeeWei").ValueKind, Is.EqualTo(JsonValueKind.Null));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_reports_a_replacement_hash()
    {
        Transaction original = DiagnosisPoolTransaction(5);
        Transaction replacement = DiagnosisPoolTransaction(5);
        replacement.Hash = TestItem.KeccakB;
        await using McpTestNode node = await DiagnosisPoolNode([replacement], lookup: original);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", original.Hash!.ToString())]);
        JsonElement result = McpAssert.Success(call);
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("replaced"));
        Assert.That(result.GetProperty("replacementHash").GetString(), Is.EqualTo(replacement.Hash.ToString()));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_rechecks_a_transaction_mined_during_lookup()
    {
        Transaction tx = DiagnosisPoolTransaction(5);
        LegacyTransactionForRpc pending = DiagnosisFeeTransaction(TxType.Blob, 20, 2);
        pending.Hash = tx.Hash;
        LegacyTransactionForRpc mined = DiagnosisFeeTransaction(TxType.Blob, 20, 2);
        mined.Hash = tx.Hash;
        mined.BlockNumber = 7;
        mined.TransactionIndex = 0;
        await using McpTestNode node = await DiagnosisPoolNode([tx], latest: 6, lookup: tx, configureEth: eth =>
        {
            eth.eth_getTransactionByHash(Arg.Any<Hash256>()).Returns(
                ResultWrapper<TransactionForRpc?>.Success(pending), ResultWrapper<TransactionForRpc?>.Success(mined));
            eth.eth_getTransactionReceipt(Arg.Any<Hash256>()).Returns(ResultWrapper<ReceiptForRpc?>.Success(new ReceiptForRpc { Status = 1 }));
        });
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", tx.Hash!.ToString())]);
        Assert.That(McpAssert.Success(call).GetProperty("status").GetString(), Is.EqualTo("mined"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    [Test]
    public async Task Diagnose_not_found_includes_the_receipt_history_limit()
    {
        await using McpTestNode node = await McpTestNode.Create();
        await node.Seed();
        Block older = (await node.Seed()).Block;
        Block head = (await node.Seed()).Block;
        for (ulong number = 1; number <= older.Number; number++)
            node.Chain.ReceiptStorage.RemoveReceipts(node.Chain.BlockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical)!);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, "diagnose_transaction", [("hash", UnknownHash)]);
        Assert.That(McpAssert.Success(call).GetProperty("summary").GetString(),
            Does.Contain($"transaction history only from block {head.Number}"));
        await McpToolCalls.AssertConformsToOutputSchema(client, "diagnose_transaction", call);
    }

    private sealed class DepositRpc : TransactionForRpc
    {
        public override TxType? Type => TxType.DepositTx;
        public override bool ShouldSetBaseFee() => false;
        public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null) =>
            new Transaction { Type = TxType.DepositTx, SenderAddress = TestItem.AddressB, To = TestItem.AddressD, GasLimit = 21_000 };
    }

    [Test]
    public async Task Mined_deposit_is_recognised_by_diagnosis_and_explanation([Values("diagnose_transaction", "explain_transaction")] string tool)
    {
        await using McpTestNode node = await McpTestNode.Create(configureContainer: builder => builder
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        Block block = (await node.Seed()).Block;
        IEthRpcModule eth = Substitute.For<IEthRpcModule>();
        eth.eth_getHeaderByNumber(Arg.Any<BlockParameter>()).Returns(ResultWrapper<BlockHeaderForRpc?>.Success(null));
        eth.eth_getTransactionByHash(Arg.Any<Hash256>()).Returns(ResultWrapper<TransactionForRpc?>.Success(
            new DepositRpc { Hash = TestItem.KeccakA, BlockNumber = block.Number, BlockHash = block.Hash, TransactionIndex = 0 }));
        eth.eth_getTransactionReceipt(Arg.Any<Hash256>()).Returns(ResultWrapper<ReceiptForRpc?>.Success(
            new ReceiptForRpc { Status = 1, GasUsed = 21_000, EffectiveGasPrice = 0, Logs = [] }));
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>())
            .Override(nameof(IEthRpcModule.eth_getTransactionByHash), eth);
        await using McpClient client = await node.CreateClient();
        CallToolResult call = await McpToolCalls.Call(client, tool, [("hash", TestItem.KeccakA.ToString())]);
        Assert.That(call.IsError, Is.Not.True, () => "a mined deposit is a known transaction: " + JsonSerializer.Serialize(call));
        Assert.That(McpAssert.Success(call).GetProperty("status").GetString(), Is.EqualTo(tool == "diagnose_transaction" ? "mined" : "success"));
        await McpToolCalls.AssertConformsToOutputSchema(client, tool, call);
    }

    [TestCase(41UL, 20UL, 2UL, 0UL, "pending_ready")]
    [TestCase(43UL, 20UL, 2UL, 0UL, "pending_queued")]
    [TestCase(41UL, 5UL, 2UL, 0UL, "underpriced")]
    [TestCase(41UL, 20UL, 2UL, 1UL, "blob_underpriced")]
    [TestCase(40UL, 20UL, 2UL, 0UL, "nonce_too_low")]
    public async Task Diagnose_classifies_pending_fees_and_nonces_with_schema(
        ulong nonce, ulong maxFee, ulong priority, ulong maxBlobFee, string expected)
    {
        EIP1559TransactionForRpc transaction = maxBlobFee == 0
            ? new EIP1559TransactionForRpc()
            : new BlobTransactionForRpc { MaxFeePerBlobGas = maxBlobFee };
        transaction.Nonce = nonce;
        transaction.MaxFeePerGas = maxFee;
        transaction.MaxPriorityFeePerGas = priority;
        JsonObject diagnosis = McpDiagnosisTools.Diagnose(transaction, 41, 41, new McpDiagnosisTools.FeeSnapshot(10, 5, 1, 1, 1));
        diagnosis["canSend"] = false;
        JsonObject envelope = new() { ["result"] = diagnosis };
        McpClientTool tool = (await _client.ListToolsAsync()).Single(static t => t.Name == "diagnose_transaction");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diagnosis["status"]!.GetValue<string>(), Is.EqualTo(expected));
            if (expected is "underpriced" or "blob_underpriced")
                Assert.That(diagnosis["recommendations"]!.ToJsonString(), Does.Contain("wei").And.Contain("gwei"));
        }

        McpSchemaValidator.AssertConforms(tool.ProtocolTool.OutputSchema!.Value, JsonSerializer.SerializeToElement(envelope));
    }

    [Test]
    public async Task Address_activity_reports_token_movement_and_schema()
    {
        string block = _scenario.Block.Number.ToString();
        CallToolResult call = await McpToolCalls.Call(_client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("fromBlock", block), ("toBlock", block), ("token", _scenario.TokenContract.ToString())]);
        JsonElement result = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("movements").GetArrayLength(), Is.GreaterThanOrEqualTo(1));
            Assert.That(result.GetProperty("pageTransactions").GetInt32(), Is.GreaterThanOrEqualTo(1));
            Assert.That(result.GetProperty("nativeTransfersIncluded").GetBoolean(), Is.False);
        }

        await McpToolCalls.AssertConformsToOutputSchema(_client, "address_activity", call);
    }

    [Test]
    public async Task Address_activity_cursor_pages_merged_transfer_stream_without_gaps()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.MaxLogBlockRange = 1);
        McpTxScenario scenario = await McpTxScenario.Create(node);
        PrivateKey sender = TestItem.PrivateKeyB;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        Transaction first = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce).WithGasPrice(1)
            .WithTo(scenario.TokenContract).WithData(McpTxScenario.TransferCallData).WithGasLimit(100_000)
            .SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
        Transaction second = Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(nonce + 1).WithGasPrice(1)
            .WithTo(scenario.TokenContract).WithData(McpTxScenario.TransferCallData).WithGasLimit(100_000)
            .SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
        Block later = await node.Chain.AddBlock(first, second);
        await using McpClient client = await node.CreateClient();
        List<string?> hashes = [];
        string? cursor = null;
        do
        {
            List<(string, object?)> args =
            [
                ("address", sender.Address.ToString()), ("token", scenario.TokenContract.ToString()),
                ("fromBlock", scenario.Block.Number.ToString()), ("toBlock", later.Number.ToString()), ("limit", 1), ("order", "asc")
            ];
            if (cursor is not null) args.Add(("cursor", cursor));
            CallToolResult call = await McpToolCalls.Call(client, "address_activity", [.. args]);
            JsonElement page = McpAssert.Success(call);
            hashes.AddRange(page.GetProperty("movements").EnumerateArray().Select(static item => item.GetProperty("transactionHash").GetString()));
            cursor = page.GetProperty("truncated").GetBoolean() ? page.GetProperty("nextCursor").GetString() : null;
            await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
            Assert.That(hashes, Has.Count.LessThan(10), "paging must terminate");
        }
        while (cursor is not null);

        Assert.That(hashes, Is.EqualTo(new[] { scenario.TokenCall.Hash!.ToString(), first.Hash!.ToString(), second.Hash!.ToString() }));
    }

    [Test]
    public async Task Address_activity_rejects_a_missing_receipt_in_the_requested_range()
    {
        await using McpTestNode node = await McpTestNode.Create();
        McpTxScenario scenario = await McpTxScenario.Create(node);
        node.Chain.ReceiptStorage.RemoveReceipts(scenario.Block);
        await using McpClient client = await node.CreateClient();

        JsonElement error = McpAssert.Error(await McpToolCalls.Call(client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("fromBlock", scenario.Block.Number.ToString()),
                ("toBlock", scenario.Block.Number.ToString())]), McpAssert.Unavailable);

        Assert.That(error.GetProperty("message").GetString(), Does.Contain($"block {scenario.Block.Number}"));
    }

    [Test]
    public async Task Address_activity_clamps_to_the_receipt_history_floor()
    {
        await using McpTestNode node = await McpTestNode.Create();
        ulong first = (await node.Seed()).Block.Number;
        ulong second = (await node.Seed()).Block.Number;
        ulong third = (await node.Seed()).Block.Number;
        for (ulong number = 1; number <= second; number++)
            node.Chain.ReceiptStorage.RemoveReceipts(node.Chain.BlockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical)!);
        await using McpClient client = await node.CreateClient();

        CallToolResult call = await McpToolCalls.Call(client, "address_activity",
            [("address", TestItem.AddressB.ToString()), ("fromBlock", first.ToString()), ("toBlock", third.ToString())]);
        JsonElement result = McpAssert.Success(call);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("clampedFromBlock").GetUInt64(), Is.EqualTo(first));
            Assert.That(result.GetProperty("fromBlock").GetUInt64(), Is.EqualTo(third));
            Assert.That(result.GetProperty("note").GetString(), Does.Contain("receipt history"));
        }

        await McpToolCalls.AssertConformsToOutputSchema(client, "address_activity", call);
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
        ulong overHalfGas = (ulong)_node.Config.MaxCallGas / 2 + 1;
        string excessiveBatchGas = $$"""[{"to":"{{to}}","gas":"{{overHalfGas}}"},{"to":"{{to}}","gas":"{{overHalfGas}}"}]""";
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
            [("calls", JsonDocument.Parse(excessiveBatchGas).RootElement)],
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

    [TestCase(1, "[{\"to\":\"0x0000000000000000000000000000000000000001\"},{\"to\":\"0x0000000000000000000000000000000000000002\"}]")]
    [TestCase(10, "[{\"to\":\"0x0000000000000000000000000000000000000001\",\"gas\":\"9\"},{\"to\":\"0x0000000000000000000000000000000000000002\"},{\"to\":\"0x0000000000000000000000000000000000000003\"}]")]
    public async Task Simulate_rejects_batches_that_cannot_assign_positive_gas_within_the_aggregate_limit(long maxCallGas, string calls)
    {
        await using McpTestNode node = await McpTestNode.Create(config => config.MaxCallGas = maxCallGas);
        await using McpClient client = await node.CreateClient();

        CallToolResult result = await McpToolCalls.Call(client, "simulate_transaction",
            [("calls", JsonDocument.Parse(calls).RootElement)]);

        JsonElement error = McpAssert.Error(result, McpAssert.InvalidInput);
        Assert.That(error.GetProperty("message").GetString(), Does.Contain("cannot assign at least 1 gas"));
    }

    [Test]
    public void Simulation_gas_distribution_stays_within_the_aggregate_limit()
    {
        bool valid = McpTransactionTools.TryAssignSimulationGas([3, null, null, null], 10, out ulong[] assigned, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(assigned, Is.EqualTo(new ulong[] { 3, 3, 2, 2 }));
            Assert.That(assigned.Aggregate(0UL, static (sum, gas) => sum + gas), Is.EqualTo(10));
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
