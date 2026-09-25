// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autofac;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Wallet;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// A test chain built from the production container (<see cref="BasicTestBlockchain"/>) with the production
/// <see cref="McpModule"/> loaded, and the <see cref="McpHost"/> resolved from it.
/// </summary>
internal sealed class McpTestNode : IAsyncDisposable
{
    public const string TestToken = "0123456789abcdef0123456789abcdef-mcp-test-token";

    private readonly TempPath? _tokenFile;

    private McpTestNode(BasicTestBlockchain chain, McpConfig config, TempPath? tokenFile)
    {
        Chain = chain;
        Config = config;
        _tokenFile = tokenFile;
    }

    public BasicTestBlockchain Chain { get; }

    public McpConfig Config { get; }

    public McpHost Host => Chain.Container.Resolve<McpHost>();

    public Uri Endpoint => Host.Endpoint ?? throw new InvalidOperationException("The MCP host is not running.");

    private static readonly Lock WalletLock = new();

    /// <summary>Creates the chain, and starts the MCP listener on an ephemeral loopback port unless <paramref name="start"/> is false.</summary>
    /// <param name="configure">Adjusts the MCP config; it starts as enabled on port 0.</param>
    /// <param name="configureContainer">Extra container overrides, applied after the MCP module.</param>
    /// <param name="withAuth">Writes <see cref="TestToken"/> to a temporary file and sets it as <see cref="IMcpConfig.AuthTokenFile"/>.</param>
    /// <param name="start">Whether to start the MCP listener.</param>
    public static async Task<McpTestNode> Create(
        Action<McpConfig>? configure = null,
        Action<ContainerBuilder>? configureContainer = null,
        bool withAuth = false,
        bool start = true)
    {
        McpConfig config = new() { Enabled = true, Port = 0 };
        TempPath? tokenFile = null;
        if (withAuth)
        {
            tokenFile = TempPath.GetTempFile();
            await File.WriteAllTextAsync(tokenFile.Path, TestToken);
            config.AuthTokenFile = tokenFile.Path;
        }

        configure?.Invoke(config);

        // Berlin: REVERT, typed receipts with status and EIP-155, and no base fee to juggle.
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
        {
            builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance))
                .AddModule(new McpModule())
                .AddSingleton<IMcpConfig>(config);
            configureContainer?.Invoke(builder);
        });

        // DevWallet's constructor mutates a static key seed, so concurrent test nodes creating it race and the eth module
        // fails to activate; creating the singleton up front, one node at a time, keeps parallel fixtures deterministic.
        lock (WalletLock)
        {
            chain.Container.Resolve<IWallet>();
        }

        McpTestNode node = new(chain, config, tokenFile);
        if (start)
        {
            try
            {
                await node.Host.StartAsync(CancellationToken.None);
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        return node;
    }

    /// <summary>Connects the official MCP SDK client over Streamable HTTP.</summary>
    public Task<McpClient> CreateClient(string? bearerToken = null, CancellationToken cancellationToken = default) =>
        CreateClient(Endpoint, bearerToken, cancellationToken);

    public static async Task<McpClient> CreateClient(Uri endpoint, string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        HttpClientTransportOptions options = new()
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "Nethermind.Mcp.Plugin.Test",
        };
        if (bearerToken is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" };
        }

        return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Adds one block holding a value transfer and three contract deployments: one emitting two logs, one whose
    /// code returns <see cref="SeededChain.ReturnWord"/>, and one whose code reverts with <see cref="SeededChain.RevertData"/>.
    /// </summary>
    public async Task<SeededChain> Seed()
    {
        BasicTestBlockchain chain = Chain;
        PrivateKey sender = TestItem.PrivateKeyB;
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, sender.Address);

        byte[] logInitCode = Prepare.EvmCode
            .StoreDataInMemory(0, SeededChain.LogData)
            .Log(32, 0, [SeededChain.TopicA])
            // LOGn pops topics last-pushed first, so this emits topics [TopicA, TopicB].
            .Log(32, 0, [SeededChain.TopicB, SeededChain.TopicA])
            .Done;
        byte[] returnInitCode = Prepare.EvmCode.ForInitOf(SeededChain.ReturnRuntimeCode).Done;
        byte[] revertRuntimeCode = Prepare.EvmCode.StoreDataInMemory(0, SeededChain.RevertData).Revert(SeededChain.RevertData.Length, 0).Done;
        byte[] revertInitCode = Prepare.EvmCode.ForInitOf(revertRuntimeCode).Done;

        Transaction transfer = Tx(nonce).WithTo(TestItem.AddressC).WithValue(SeededChain.TransferValue).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(chain.EthereumEcdsa, sender).TestObject;
        Transaction logDeploy = Deploy(nonce + 1, logInitCode);
        Transaction returnDeploy = Deploy(nonce + 2, returnInitCode);
        Transaction revertDeploy = Deploy(nonce + 3, revertInitCode);

        Block block = await chain.AddBlock(transfer, logDeploy, returnDeploy, revertDeploy);
        Assert.That(block.Transactions, Has.Length.EqualTo(4), "precondition: every seeded transaction must be mined");

        return new SeededChain(
            block,
            transfer,
            logDeploy,
            ContractAddress.From(sender.Address, nonce + 1),
            ContractAddress.From(sender.Address, nonce + 2),
            ContractAddress.From(sender.Address, nonce + 3));

        TransactionBuilder<Transaction> Tx(ulong txNonce) =>
            Build.A.Transaction.WithChainId(chain.SpecProvider.ChainId).WithNonce(txNonce).WithGasPrice(1);

        Transaction Deploy(ulong txNonce, byte[] initCode) =>
            Tx(txNonce).WithCode(initCode).WithGasLimit(200_000).SignedAndResolved(chain.EthereumEcdsa, sender).TestObject;
    }

    public async ValueTask DisposeAsync()
    {
        // The host is async-disposable only, so the container must be disposed asynchronously, as the runner does.
        await Chain.BlockProducerRunner.StopAsync();
        await Chain.Container.DisposeAsync();
        _tokenFile?.Dispose();
    }
}

/// <summary>What <see cref="McpTestNode.Seed"/> put on chain.</summary>
internal sealed record SeededChain(
    Block Block,
    Transaction Transfer,
    Transaction LogDeploy,
    Address LogContract,
    Address ReturnContract,
    Address RevertContract)
{
    public static readonly UInt256 TransferValue = 1234;
    public static readonly Hash256 TopicA = Keccak.Compute("Nethermind.Mcp.TopicA");
    public static readonly Hash256 TopicB = Keccak.Compute("Nethermind.Mcp.TopicB");
    public static readonly byte[] LogData = Bytes.FromHexString("0x000000000000000000000000000000000000000000000000000000000000002a");
    public static readonly byte[] ReturnWord = Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000cafebabe");
    public static readonly byte[] RevertData = Bytes.FromHexString("0xdeadbeef");
    public static readonly byte[] ReturnRuntimeCode = Prepare.EvmCode.StoreDataInMemory(0, ReturnWord).Return(32, 0).Done;
}

/// <summary>
/// Decorates the production <see cref="IRpcModuleProvider"/> so a test can hand out its own module for chosen
/// methods, or make renting them fail, while every other method keeps using the real pools.
/// </summary>
internal sealed class FaultInjectingRpcModuleProvider(IRpcModuleProvider inner) : IRpcModuleProvider
{
    private readonly Dictionary<string, Func<ValueTask<IRpcModule>>> _overrides = new(StringComparer.Ordinal);
    private readonly HashSet<IRpcModule> _injected = new(ReferenceEqualityComparer.Instance);
    private readonly Lock _lock = new();
    private int _rented;
    private int _returned;

    /// <summary>Gets how many injected modules were rented.</summary>
    public int Rented => Volatile.Read(ref _rented);

    /// <summary>Gets how many injected modules were returned.</summary>
    public int Returned => Volatile.Read(ref _returned);

    public void Override(string methodName, IRpcModule module)
    {
        lock (_lock)
        {
            _injected.Add(module);
            _overrides[methodName] = () => ValueTask.FromResult(module);
        }
    }

    public void Throw(string methodName, Exception exception)
    {
        lock (_lock) _overrides[methodName] = () => ValueTask.FromException<IRpcModule>(exception);
    }

    public IJsonSerializer Serializer => inner.Serializer;
    public IReadOnlyCollection<string> Enabled => inner.Enabled;
    public IReadOnlyCollection<string> All => inner.All;

    public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule => inner.Register(pool);

    public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module, out ResolvedMethodInfo? method) =>
        inner.Check(methodName, context, out module, out method);

    public ResolvedMethodInfo? Resolve(string methodName) => inner.Resolve(methodName);

    public ValueTask<IRpcModule> Rent(string methodName, bool canBeShared)
    {
        Func<ValueTask<IRpcModule>>? rent;
        lock (_lock) _overrides.TryGetValue(methodName, out rent);
        if (rent is null) return inner.Rent(methodName, canBeShared);

        Interlocked.Increment(ref _rented);
        return rent();
    }

    public ValueTask<IRpcModule> Rent(ResolvedMethodInfo method)
    {
        bool overridden;
        lock (_lock) overridden = _overrides.ContainsKey(method.MethodInfo.Name);
        return overridden ? Rent(method.MethodInfo.Name, method.ReadOnly) : inner.Rent(method);
    }

    public void Return(string methodName, IRpcModule rpcModule)
    {
        if (IsInjected(rpcModule))
        {
            Interlocked.Increment(ref _returned);
            return;
        }

        inner.Return(methodName, rpcModule);
    }

    public void Return(ResolvedMethodInfo method, IRpcModule rpcModule)
    {
        if (IsInjected(rpcModule))
        {
            Interlocked.Increment(ref _returned);
            return;
        }

        inner.Return(method, rpcModule);
    }

    private bool IsInjected(IRpcModule module)
    {
        lock (_lock) return _injected.Contains(module);
    }
}

/// <summary>Assertions over MCP tool results and raw MCP HTTP exchanges.</summary>
internal static partial class McpAssert
{
    public static readonly string[] ToolNames =
    [
        "block_summary", "call", "call_function", "chain_info", "decode_logs", "estimate_gas", "explain_transaction", "fee_estimate",
        "get_balance", "get_block", "get_block_receipts", "get_code", "get_logs", "get_proof", "get_storage_at", "get_transaction",
        "get_transaction_receipt", "lookup_address", "node_status", "resolve_ens", "simulate_transaction", "token_balances", "token_info",
        "trace_transaction"
    ];

    public const string InvalidInput = "invalid_input";
    public const string NotFound = "not_found";
    public const string ExecutionReverted = "execution_reverted";
    public const string ResourceExhausted = "resource_exhausted";
    public const string Timeout = "timeout";
    public const string Unavailable = "unavailable";
    public const string InternalError = "internal_error";

    /// <summary>Asserts a successful result and returns its <c>result</c> member.</summary>
    public static JsonElement Success(CallToolResult result)
    {
        Assert.That(result.IsError, Is.Not.True, () => $"tool failed: {Text(result)}");
        JsonElement structured = Structured(result);
        Assert.That(structured.TryGetProperty("result", out JsonElement value), Is.True, () => $"missing 'result' in {structured}");
        return value;
    }

    /// <summary>Asserts a failed result carrying <paramref name="expectedCode"/> and returns the error object.</summary>
    /// <remarks>Errors are text-only: structuredContent would have to match the tool's output schema, which describes success.</remarks>
    public static JsonElement Error(CallToolResult result, params string[] expectedCode)
    {
        Assert.That(result.IsError, Is.True, () => $"expected a tool error, got {Text(result)}");
        Assert.That(result.StructuredContent, Is.Null, "errors must not carry structuredContent");
        using JsonDocument document = JsonDocument.Parse(Text(result));
        Assert.That(document.RootElement.TryGetProperty("error", out JsonElement found), Is.True, () => $"missing 'error' in {document.RootElement}");
        JsonElement error = found.Clone();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("code").GetString(), Is.AnyOf(expectedCode), () => error.ToString());
            Assert.That(error.GetProperty("message").GetString(), Is.Not.Null.And.Not.Empty);
        }

        return error;
    }

    private static string Text(CallToolResult result)
    {
        TextContentBlock? text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        Assert.That(text, Is.Not.Null, "content must carry a text block");
        return text!.Text;
    }

    /// <summary>Asserts the result carries structured content mirrored by a single JSON text block, and returns the structured content.</summary>
    public static JsonElement Structured(CallToolResult result)
    {
        Assert.That(result.StructuredContent, Is.Not.Null, "structuredContent must be set");
        JsonElement structured = result.StructuredContent!.Value;
        Assert.That(structured.ValueKind, Is.EqualTo(JsonValueKind.Object));

        TextContentBlock? text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        Assert.That(text, Is.Not.Null, "content must carry a text block for clients without structured output");
        using JsonDocument textJson = JsonDocument.Parse(text!.Text);
        Assert.That(JsonElement.DeepEquals(textJson.RootElement, structured), Is.True, "text content must mirror structuredContent");
        return structured;
    }

    /// <summary>Asserts that a client-visible error leaks no exception type or stack trace.</summary>
    public static void NoInternals(JsonElement error, params string[] secrets)
    {
        string text = error.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain("Exception"), "exception type names must not reach clients");
            Assert.That(text, Does.Not.Contain(" at "), "stack traces must not reach clients");
            Assert.That(text, Does.Not.Contain(".cs:line"), "stack traces must not reach clients");
            foreach (string secret in secrets) Assert.That(text, Does.Not.Contain(secret));
        }
    }

    /// <summary>Asserts a JSON-RPC quantity: 0x-prefixed lowercase hex without leading zeros.</summary>
    public static void Quantity(JsonElement value, string? expected = null)
    {
        string? text = value.GetString();
        Assert.That(text, Does.Match(QuantityRegex()), "quantity encoding");
        if (expected is not null) Assert.That(text, Is.EqualTo(expected));
    }

    /// <summary>Asserts 0x-prefixed lowercase hex data of <paramref name="bytes"/> bytes (any length when null).</summary>
    public static void Data(JsonElement value, int? bytes = null)
    {
        string? text = value.GetString();
        Assert.That(text, Does.Match(DataRegex()), "data encoding");
        if (bytes is not null) Assert.That(text!.Length, Is.EqualTo(2 + 2 * bytes.Value), "data length");
    }

    public static string Hex(UInt256 value) => value.ToHexString(true);

    public static string Hex(ulong value) => ((UInt256)value).ToHexString(true);

    [GeneratedRegex("^0x(0|[1-9a-f][0-9a-f]*)$")]
    private static partial Regex QuantityRegex();

    [GeneratedRegex("^0x([0-9a-f]{2})*$")]
    private static partial Regex DataRegex();
}

/// <summary>Raw HTTP exchanges with the MCP endpoint, for the transport-level guards.</summary>
internal static class McpHttp
{
    public const string ProtocolVersion = "2025-06-18";

    public static string InitializeBody(string clientName = "raw-http-test") =>
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":""" + $"\"{ProtocolVersion}\"" +
        ""","capabilities":{},"clientInfo":{"name":""" + $"\"{clientName}\"" + ""","version":"1.0.0"}}}""";

    public const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";

    public static HttpRequestMessage Post(Uri uri, string body, string? origin = null, string? host = null, string? bearer = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    /// <summary>Reads a JSON-RPC response from either a JSON body or a single-event SSE stream.</summary>
    public static async Task<JsonElement> ReadJsonRpc(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        string json = body;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            json = string.Join("\n", body
                .Split('\n')
                .Where(static line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(static line => line["data:".Length..].Trim()));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static void AssertNoCors(HttpResponseMessage response)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.False, "no CORS headers may ever be emitted");
            Assert.That(response.Headers.Contains("Access-Control-Allow-Credentials"), Is.False, "no CORS headers may ever be emitted");
        }
    }
}
