// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.State;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// <c>node_status</c> end to end on the test chain, and the executor's mapping of unavailable-data failures to
/// range-aware <c>unavailable</c> errors.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpNodeToolsTests
{
    private const string Secret = "internal-secret-detail";

    private McpTestNode _node = null!;
    private McpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _node = await McpTestNode.Create(c => c.MaxConcurrentToolCalls = 64);
        await _node.Seed();
        _client = await _node.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
    }

    [Test]
    public async Task Node_status_is_listed_as_a_read_only_tool_with_an_output_schema()
    {
        McpClientTool tool = (await _client.ListToolsAsync()).Single(static t => t.Name == "node_status");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tool.ProtocolTool.Annotations?.ReadOnlyHint, Is.True);
            Assert.That(tool.ProtocolTool.Annotations?.DestructiveHint, Is.False);
            Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null);
            Assert.That(tool.Description, Does.Contain("xDAI"));
            Assert.That(tool.ProtocolTool.InputSchema.TryGetProperty("required", out JsonElement required) ? required.GetArrayLength() : 0, Is.Zero);
        }
    }

    [Test]
    public async Task Node_status_reports_chain_client_head_and_capabilities()
    {
        JsonElement status = McpAssert.Success(await NodeStatus());
        BlockHeader head = _node.Chain.BlockTree.Head!.Header;

        JsonElement chain = status.GetProperty("chain");
        JsonElement headJson = status.GetProperty("head");
        JsonElement sync = status.GetProperty("sync");
        JsonElement state = status.GetProperty("state");
        JsonElement history = status.GetProperty("history");
        JsonElement features = status.GetProperty("features");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.GetProperty("chainIdDecimal").GetUInt64(), Is.EqualTo(_node.Chain.SpecProvider.ChainId));
            McpAssert.Quantity(chain.GetProperty("chainId"), McpAssert.Hex(_node.Chain.SpecProvider.ChainId));
            Assert.That(chain.GetProperty("nativeCurrency").GetString(), Is.EqualTo("ETH"));
            Assert.That(chain.GetProperty("networkName").GetString(), Is.Not.Empty);
            Assert.That(status.GetProperty("client").GetProperty("version").GetString(), Is.EqualTo(ProductInfo.Version));

            Assert.That(headJson.GetProperty("number").GetUInt64(), Is.EqualTo(head.Number));
            McpAssert.Quantity(headJson.GetProperty("numberHex"), McpAssert.Hex(head.Number));
            Assert.That(headJson.GetProperty("hash").GetString(), Is.EqualTo(head.Hash!.ToString()));
            Assert.That(headJson.GetProperty("timestamp").GetUInt64(), Is.EqualTo(head.Timestamp));
            Assert.That(DateTimeOffset.Parse(headJson.GetProperty("timestampIso").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
                Is.EqualTo((long)head.Timestamp));
            Assert.That(headJson.GetProperty("ageSeconds").GetInt64(), Is.GreaterThanOrEqualTo(0));

            Assert.That(sync.GetProperty("headStateAvailable").GetBoolean(), Is.True);
            Assert.That(sync.GetProperty("highestBlock").GetUInt64(), Is.GreaterThanOrEqualTo(head.Number));

            Assert.That(state.GetProperty("backend").GetString(), Is.AnyOf("Flat", "HalfPath", "Hash"));
            Assert.That(state.GetProperty("summary").GetString(), Is.Not.Empty);
            Assert.That(history.GetProperty("receiptsStored").GetBoolean(), Is.True);
            Assert.That(features.GetProperty("logIndex").GetProperty("enabled").ValueKind, Is.AnyOf(JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null));
            Assert.That(status.GetProperty("warnings").ValueKind, Is.EqualTo(JsonValueKind.Array));
        }
    }

    [Test]
    public async Task Node_status_conforms_to_its_output_schema()
    {
        CallToolResult result = await NodeStatus();
        McpAssert.Success(result);

        await McpToolCalls.AssertConformsToOutputSchema(_client, "node_status", result);
    }

    [Test]
    public async Task Node_status_warns_in_plain_english()
    {
        JsonElement status = McpAssert.Success(await NodeStatus());
        string[] warnings = status.GetProperty("warnings").EnumerateArray().Select(static w => w.GetString()!).ToArray();

        using (Assert.EnterMultipleScope())
        {
            if (status.GetProperty("peers").GetProperty("count") is { ValueKind: JsonValueKind.Number } count && count.GetInt32() == 0)
            {
                Assert.That(warnings, Has.Some.Contain("0 peers"));
            }

            if (status.GetProperty("head").GetProperty("ageSeconds").GetInt64() > McpNodeTools.StaleHeadSeconds)
            {
                Assert.That(warnings, Has.Some.Contain("head block is"));
            }

            Assert.That(warnings, Has.None.Contain("Receipt.StoreReceipts"));
            Assert.That(warnings, Has.None.Contain("State for the head block"));
        }
    }

    [Test]
    public void Capabilities_on_the_test_chain_accept_every_stored_block()
    {
        McpNodeCapabilities capabilities = _node.Chain.Container.Resolve<McpNodeCapabilities>();
        ulong head = _node.Chain.BlockTree.Head!.Number;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.CheckState(head), Is.Null);
            Assert.That(capabilities.CheckBody(head), Is.Null);
            Assert.That(capabilities.CheckReceipts(head), Is.Null);
            Assert.That(capabilities.CheckState(head + 1000), Is.Null, "an unknown block is left to the tool's not_found");
            Assert.That(capabilities.GetAvailability().HeadNumber, Is.EqualTo((long)head));
            Assert.That(capabilities.GetAvailability().OldestStateBlock, Is.Not.Null);
        }
    }

    private static IEnumerable<TestCaseData> UnavailableFailures()
    {
        yield return new TestCaseData("missing trie node 0xab (path ) state 0xab is not available", ErrorCodes.ResourceNotFound, "keeps state for blocks")
            .SetName("Missing_trie_node_result_is_unavailable_with_state_range");
        yield return new TestCaseData("No state available for block 0x12 (18)", ErrorCodes.ResourceUnavailable, "keeps state for blocks")
            .SetName("No_state_result_is_unavailable_with_state_range");
        yield return new TestCaseData(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable, "keeps block bodies from")
            .SetName("Pruned_history_result_is_unavailable_with_history_range");
    }

    [TestCaseSource(nameof(UnavailableFailures))]
    public void Unavailable_results_carry_the_available_range(string message, int code, string hint)
    {
        McpToolExecutor executor = _node.Chain.Container.Resolve<McpToolExecutor>();

        JsonElement error = McpAssert.Error(executor.Failure("test", ResultWrapper<UInt256?>.Fail(message, code)), McpAssert.Unavailable);

        string text = error.GetProperty("message").GetString()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.StartWith(message));
            Assert.That(text, Does.Contain(hint));
        }
    }

    [TestCase(BlockFinderExtensions.HeaderNotFound, McpAssert.NotFound)]
    [TestCase("invalid argument", McpAssert.InvalidInput)]
    public void Other_default_code_results_keep_their_mapping(string message, string expected)
    {
        McpToolExecutor executor = _node.Chain.Container.Resolve<McpToolExecutor>();

        JsonElement error = McpAssert.Error(executor.Failure("test", ResultWrapper<UInt256?>.Fail(message, ErrorCodes.Default)), expected);

        Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(message));
    }

    private static IEnumerable<TestCaseData> UnavailableExceptions()
    {
        yield return new TestCaseData(new MissingTrieNodeException(Secret, null, TreePath.Empty, Keccak.Zero, new StateNotRetainedException(Secret)), "keeps state for blocks")
            .SetName("State_not_retained_trie_node_is_unavailable");
        yield return new TestCaseData(new MissingTrieNodeException(Secret, null, TreePath.Empty, Keccak.Zero), "keeps state for blocks")
            .SetName("Missing_trie_node_is_unavailable");
        yield return new TestCaseData(new StateNotRetainedException(Secret), "keeps state for blocks")
            .SetName("State_not_retained_is_unavailable");
        yield return new TestCaseData(new StateUnavailableException(Secret), "keeps state for blocks")
            .SetName("State_unavailable_is_unavailable");
        yield return new TestCaseData(new ResourceNotFoundException(Secret), "keeps block bodies from")
            .SetName("Resource_not_found_is_unavailable_with_history_range");
    }

    [TestCaseSource(nameof(UnavailableExceptions))]
    public async Task Unavailable_exceptions_become_range_aware_errors(Exception failure, string hint)
    {
        McpToolExecutor executor = _node.Chain.Container.Resolve<McpToolExecutor>();

        JsonElement error = McpAssert.Error(
            await executor.ExecuteLocalAsync("test", _ => Task.FromException<CallToolResult>(failure), CancellationToken.None),
            McpAssert.Unavailable);

        McpAssert.NoInternals(error, Secret);
        string text = error.GetProperty("message").GetString()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Contain(hint));
            Assert.That(text, Does.Contain("archive node").Or.Contain("full history"));
        }
    }

    [Test]
    public async Task Local_body_failure_is_internal_error_without_details()
    {
        McpToolExecutor executor = _node.Chain.Container.Resolve<McpToolExecutor>();

        JsonElement error = McpAssert.Error(
            await executor.ExecuteLocalAsync("test", _ => throw new InvalidOperationException(Secret), CancellationToken.None),
            McpAssert.InternalError);

        McpAssert.NoInternals(error, Secret, nameof(InvalidOperationException));
    }

    [Test]
    public void Local_body_client_cancellation_propagates()
    {
        McpToolExecutor executor = _node.Chain.Container.Resolve<McpToolExecutor>();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
            () => executor.ExecuteLocalAsync("test", _ => Task.FromResult(executor.Success(1)), cts.Token));
    }

    [Test]
    public async Task Local_body_times_out_at_the_tool_timeout()
    {
        await using McpTestNode node = await McpTestNode.Create(c => c.ToolTimeout = 50, start: false);
        McpToolExecutor executor = node.Chain.Container.Resolve<McpToolExecutor>();
        TaskCompletionSource<CallToolResult> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        McpAssert.Error(await executor.ExecuteLocalAsync("test", _ => never.Task, CancellationToken.None), McpAssert.Timeout);
        never.SetResult(executor.Success(1));
    }

    [Test]
    public async Task Pruned_state_from_the_eth_module_reaches_the_client_with_the_state_range()
    {
        IEthRpcModule module = Substitute.For<IEthRpcModule>();
        module.eth_getBalance(Arg.Any<Address>(), Arg.Any<BlockParameter?>())
            .Returns(Task.FromResult(ResultWrapper<UInt256?>.Fail("No state available for block 0x1 (1)", ErrorCodes.ResourceUnavailable)));
        await using McpTestNode node = await McpTestNode.Create(configureContainer: static builder => builder
            .AddDecorator<IRpcModuleProvider>(static (_, inner) => new FaultInjectingRpcModuleProvider(inner)));
        ((FaultInjectingRpcModuleProvider)node.Chain.Container.Resolve<IRpcModuleProvider>()).Override(nameof(IEthRpcModule.eth_getBalance), module);
        await using McpClient client = await node.CreateClient();

        CallToolResult result = await client.CallToolAsync("get_balance",
            new Dictionary<string, object?> { ["address"] = TestItem.AddressC.ToString(), ["block"] = "latest" });

        JsonElement error = McpAssert.Error(result, McpAssert.Unavailable);
        Assert.That(error.GetProperty("message").GetString(), Does.Contain("this node keeps state for blocks"));
    }

    private Task<CallToolResult> NodeStatus() =>
        _client.CallToolAsync("node_status", new Dictionary<string, object?>()).AsTask();
}
