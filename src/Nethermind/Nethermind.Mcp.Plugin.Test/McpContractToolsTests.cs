// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>
/// End-to-end tests of the semantic contract tools against hand-assembled contracts deployed on a test chain: an
/// ERC-20-like token (plus bytes32-symbol, ERC-721 and EIP-1967-proxy variants) and a minimal ENS registry and resolver
/// placed at the canonical registry address in genesis.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class McpContractToolsTests
{
    private static readonly Address EnsResolver = new("0x00000000000000000000000000000000000e5501");
    private static readonly Address EnsRegistry = new("0x00000000000C2E074eC69A0dFb2997BA6C7d2e1e");
    private static readonly Address Holder = TestItem.AddressC;
    private static readonly Address EnsTarget = TestItem.AddressB;
    private static readonly Address Implementation = new("0x1234567890123456789012345678901234567890");
    private static readonly UInt256 Supply = 1_000_000_000;
    private const string ZosImplementationSlot = "0x7050c9e0f4ca769c69bd3a8ef740bc37934f8e2c036e5a723fd8ee048ed3f8c3";

    // A name with an invisible Unicode TAG payload ("ASCII smuggling"), a bidi override, a zero-width joiner and an emoji.
    private const string EvilName = "Good󠁉󠁇󠁎‮gnp.exe‍ Token 🚀";

    private McpTestNode _node = null!;
    private McpClient _client = null!;
    private McpTestNode _gnosis = null!;
    private McpClient _gnosisClient = null!;
    private Deployed _deployed = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // The default test chain has chain ID 1, so ENS tools use the canonical registry address, which genesis fills in.
        _node = await McpTestNode.Create(c => c.MaxConcurrentToolCalls = 64,
            builder => builder.AddScoped<IGenesisPostProcessor, EnsGenesis>());
        _deployed = await Deploy(_node);
        _client = await _node.CreateClient();

        _gnosis = await McpTestNode.Create(configureContainer: builder =>
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance) { ChainId = BlockchainIds.Gnosis }));
        _gnosisClient = await _gnosis.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_node is not null) await _node.DisposeAsync();
        if (_gnosisClient is not null) await _gnosisClient.DisposeAsync();
        if (_gnosis is not null) await _gnosis.DisposeAsync();
    }

    [Test]
    public async Task Tools_are_listed_with_read_only_annotations_and_output_schemas()
    {
        IList<McpClientTool> tools = await _client.ListToolsAsync();
        string[] names = ["call_function", "token_info", "token_balances", "decode_logs", "resolve_ens", "lookup_address"];

        foreach (string name in names)
        {
            McpClientTool? tool = tools.SingleOrDefault(t => t.Name == name);
            Assert.That(tool, Is.Not.Null, name);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tool!.ProtocolTool.Annotations?.ReadOnlyHint, Is.True, name);
                Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null, name);
                Assert.That(tool.Description, Does.Contain("Output"), name);
            }
        }
    }

    [Test]
    public async Task Call_function_encodes_arguments_and_decodes_outputs()
    {
        JsonElement result = await Success("call_function", ("to", Hex(_deployed.Token)), ("signature", "balanceOf(address owner) returns (uint256 balance)"),
            ("args", new object[] { Holder.ToString() }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("function").GetString(), Is.EqualTo("balanceOf(address)"));
            Assert.That(result.GetProperty("selector").GetString(), Is.EqualTo("0x70a08231"));
            Assert.That(result.GetProperty("to").GetString(), Is.EqualTo(_deployed.Token.ToString(true, true)));
            JsonElement output = result.GetProperty("outputs")[0];
            Assert.That(output.GetProperty("name").GetString(), Is.EqualTo("balance"));
            Assert.That(output.GetProperty("type").GetString(), Is.EqualTo("uint256"));
            Assert.That(output.GetProperty("value").GetString(), Is.EqualTo(Supply.ToString()));
            McpAssert.Quantity(result.GetProperty("blockNumber"), McpAssert.Hex((ulong)_node.Chain.BlockTree.Head!.Number));
        }
    }

    [Test]
    public async Task Call_function_decodes_multiple_outputs_and_keeps_raw_data_without_return_types()
    {
        JsonElement reserves = await Success("call_function", ("to", Hex(_deployed.Token)), ("signature", "getReserves()(uint112,uint112,uint32)"));
        JsonElement raw = await Success("call_function", ("to", Hex(_deployed.Token)), ("signature", "decimals()"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reserves.GetProperty("outputs").EnumerateArray().Select(static o => o.GetProperty("value").GetString()), Is.EqualTo(new[] { "1000", "2000", "3" }));
            Assert.That(raw.GetProperty("outputs").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(raw.GetProperty("returnData").GetString(), Is.EqualTo("0x" + new string('0', 62) + "06"));
        }
    }

    [Test]
    public async Task Call_function_reports_undecodable_return_data()
    {
        JsonElement result = await Success("call_function", ("to", Hex(_deployed.Token)), ("signature", "decimals()(string)"));

        Assert.That(result.GetProperty("outputs").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(result.GetProperty("decodeError").GetString(), Does.Contain("(string)"));
    }

    [TestCase("fail()", "fail() reverted: \"nope\"", "Error")]
    [TestCase("boom()", "panic 0x11: arithmetic overflow or underflow", "Panic")]
    [TestCase("missing()", "reverted without a reason", "Empty")]
    public async Task Call_function_decodes_revert_reasons(string signature, string expected, string kind)
    {
        JsonElement error = McpAssert.Error(await Call(_client, "call_function", ("to", Hex(_deployed.Token)), ("signature", signature)), McpAssert.ExecutionReverted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.GetProperty("message").GetString(), Does.Contain(expected));
            Assert.That(error.GetProperty("reason").GetProperty("kind").GetString(), Is.EqualTo(kind));
        }
    }

    [Test]
    public async Task Call_function_on_an_account_without_code_is_not_found() =>
        McpAssert.Error(await Call(_client, "call_function", ("to", Hex(TestItem.AddressF)), ("signature", "totalSupply()(uint256)")), McpAssert.NotFound);

    [TestCase("balanceOf(Owner)", "[]", "unknown type 'Owner'")]
    [TestCase("balanceOf(address)", "[]", "expected 1 argument(s)")]
    [TestCase("balanceOf(address)", "[\"0x12\"]", "expected address")]
    [TestCase("event Transfer(address)", "[]", "must declare a function")]
    public async Task Call_function_rejects_invalid_input(string signature, string argsJson, string expected)
    {
        using JsonDocument args = JsonDocument.Parse(argsJson);
        JsonElement error = McpAssert.Error(
            await Call(_client, "call_function", ("to", Hex(_deployed.Token)), ("signature", signature), ("args", args.RootElement.Clone())),
            McpAssert.InvalidInput);

        Assert.That(error.GetProperty("message").GetString(), Does.Contain(expected));
    }

    [Test]
    public async Task Call_function_enforces_the_gas_limit() =>
        McpAssert.Error(await Call(_client, "call_function", ("to", Hex(_deployed.Token)), ("signature", "decimals()"),
            ("gas", (_node.Config.MaxCallGas + 1).ToString())), McpAssert.InvalidInput);

    [Test]
    public async Task Token_info_describes_an_erc20_token()
    {
        JsonElement result = await Success("token_info", ("token", Hex(_deployed.Token)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("standard").GetString(), Is.EqualTo("ERC-20"));
            Assert.That(result.GetProperty("name").GetString(), Is.EqualTo("Test Token"));
            Assert.That(result.GetProperty("symbol").GetString(), Is.EqualTo("TST"));
            Assert.That(result.GetProperty("decimals").GetInt32(), Is.EqualTo(6));
            McpAssert.Quantity(result.GetProperty("totalSupply"), McpAssert.Hex(Supply));
            Assert.That(result.GetProperty("totalSupplyFormatted").GetString(), Is.EqualTo("1000"));
            Assert.That(result.GetProperty("proxy").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
    }

    [Test]
    public async Task Token_info_handles_bytes32_symbols_erc721_and_proxies()
    {
        JsonElement bytes32 = await Success("token_info", ("token", Hex(_deployed.Bytes32Token)));
        JsonElement nft = await Success("token_info", ("token", Hex(_deployed.NftToken)));
        JsonElement proxy = await Success("token_info", ("token", Hex(_deployed.ProxyToken)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bytes32.GetProperty("symbol").GetString(), Is.EqualTo("MKR"));
            Assert.That(nft.GetProperty("standard").GetString(), Is.EqualTo("ERC-721"));
            Assert.That(nft.GetProperty("standardEvidence").GetString(), Does.Contain("0x80ac58cd"));
            Assert.That(proxy.GetProperty("proxy").GetProperty("type").GetString(), Is.EqualTo("EIP-1967"));
            Assert.That(proxy.GetProperty("proxy").GetProperty("implementation").GetString(), Is.EqualTo(Implementation.ToString(true, true)));
        }
    }

    [Test]
    public async Task Token_info_detects_zeppelinos_and_eip897_proxies()
    {
        JsonElement zos = (await Success("token_info", ("token", Hex(_deployed.ZosProxyToken)))).GetProperty("proxy");
        JsonElement getter = (await Success("token_info", ("token", Hex(_deployed.GetterProxyToken)))).GetProperty("proxy");
        JsonElement lookup = (await Success("lookup_address", ("address", Hex(_deployed.ZosProxyToken)))).GetProperty("proxy");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(zos.GetProperty("type").GetString(), Is.EqualTo("ZeppelinOS"));
            Assert.That(zos.GetProperty("implementation").GetString(), Is.EqualTo(Implementation.ToString(true, true)));
            Assert.That(getter.GetProperty("type").GetString(), Is.EqualTo("EIP-897"));
            Assert.That(getter.GetProperty("implementation").GetString(), Is.EqualTo(Implementation.ToString(true, true)));
            Assert.That(lookup.GetProperty("type").GetString(), Is.EqualTo("ZeppelinOS"));
        }
    }

    [Test]
    public async Task Untrusted_contract_strings_are_sanitized_in_every_output()
    {
        JsonElement call = await Success("call_function", ("to", Hex(_deployed.EvilToken)), ("signature", "name()(string)"));
        JsonElement info = await Success("token_info", ("token", Hex(_deployed.EvilToken)));

        string decoded = call.GetProperty("outputs")[0].GetProperty("value").GetString()!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.EqualTo("Goodgnp.exe Token 🚀"), "TAG characters, the bidi override and the joiner are removed; the emoji stays");
            Assert.That(info.GetProperty("name").GetString(), Is.EqualTo("Goodgnp.exe Token 🚀"));
        }
    }

    [Test]
    public async Task Token_info_on_an_account_without_code_is_not_found()
    {
        McpAssert.Error(await Call(_client, "token_info", ("token", Hex(TestItem.AddressF))), McpAssert.NotFound);
        McpAssert.Error(await Call(_client, "token_info", ("token", "0x12")), McpAssert.InvalidInput);
        McpAssert.Error(await Call(_client, "token_info", ("token", Hex(_deployed.Token)), ("block", "0xffffff")), McpAssert.NotFound);
    }

    [Test]
    public async Task Token_info_at_a_block_before_deployment_is_not_found() =>
        McpAssert.Error(await Call(_client, "token_info", ("token", Hex(_deployed.Token)), ("block", "earliest")), McpAssert.NotFound);

    [Test]
    public async Task Token_balances_reports_native_and_token_balances_with_inline_errors()
    {
        JsonElement result = await Success("token_balances", ("owner", Hex(Holder)),
            ("tokens", new[] { Hex(_deployed.Token), Hex(TestItem.AddressF), Hex(_deployed.Token) }));

        JsonElement[] tokens = [.. result.GetProperty("tokens").EnumerateArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("tokenSource").GetString(), Is.EqualTo("argument"));
            Assert.That(result.GetProperty("native").GetProperty("symbol").GetString(), Is.EqualTo("ETH"));
            McpAssert.Quantity(result.GetProperty("native").GetProperty("balance"));
            Assert.That(tokens, Has.Length.EqualTo(2), "duplicates are removed");
            Assert.That(tokens[0].GetProperty("symbol").GetString(), Is.EqualTo("TST"));
            McpAssert.Quantity(tokens[0].GetProperty("balance"), McpAssert.Hex(Supply));
            Assert.That(tokens[0].GetProperty("balanceFormatted").GetString(), Is.EqualTo("1000"));
            Assert.That(tokens[1].GetProperty("error").GetString(), Does.Contain("no contract code"));
        }
    }

    [Test]
    public async Task Token_balances_defaults_to_well_known_tokens_and_bounds_the_list()
    {
        JsonElement result = await Success("token_balances", ("owner", Hex(Holder)));
        Assert.That(result.GetProperty("tokenSource").GetString(), Is.EqualTo("well-known"));

        string[] tooMany = [.. Enumerable.Range(1, McpContractTools.MaxBalanceTokens + 1).Select(static i => $"0x{i:x40}")];
        McpAssert.Error(await Call(_client, "token_balances", ("owner", Hex(Holder)), ("tokens", tooMany)), McpAssert.InvalidInput);
    }

    [Test]
    public async Task Token_balances_uses_xdai_on_gnosis()
    {
        JsonElement result = McpAssert.Success(await Call(_gnosisClient, "token_balances", ("owner", Hex(TestItem.AddressA)), ("tokens", Array.Empty<string>())));

        Assert.That(result.GetProperty("native").GetProperty("symbol").GetString(), Is.EqualTo("xDAI"));
    }

    [Test]
    public async Task Decode_logs_decodes_a_transaction_with_token_metadata()
    {
        JsonElement result = await Success("decode_logs", ("txHash", _deployed.TokenDeploy.Hash!.ToString()));

        JsonElement log = result.GetProperty("logs")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("success"));
            Assert.That(result.GetProperty("decoded").GetInt32(), Is.EqualTo(1));
            Assert.That(log.GetProperty("event").GetString(), Is.EqualTo("Transfer"));
            Assert.That(log.GetProperty("standard").GetString(), Is.EqualTo("ERC-20"));
            Assert.That(log.GetProperty("source").GetString(), Is.EqualTo("known"));
            Assert.That(log.GetProperty("params")[1].GetProperty("value").GetString(), Is.EqualTo(Holder.ToString(true, true)));
            Assert.That(log.GetProperty("token").GetProperty("symbol").GetString(), Is.EqualTo("TST"));
            Assert.That(log.GetProperty("amountFormatted").GetString(), Is.EqualTo("1000"));
            McpAssert.Quantity(log.GetProperty("logIndex"), "0x0");
        }
    }

    [Test]
    public async Task Decode_logs_decodes_raw_logs_with_user_signatures()
    {
        McpAbiSignature ping = McpAbiSignature.Parse("event Ping(address indexed who, uint256 x)", McpAbiSignatureKind.Event);
        object[] logs =
        [
            new Dictionary<string, object> { ["address"] = Hex(_deployed.Token), ["topics"] = new[] { ping.Hash.ToString(), Topic(Holder) }, ["data"] = "0x" + new string('0', 62) + "2a" },
            new Dictionary<string, object> { ["address"] = Hex(_deployed.Token), ["topics"] = new[] { Keccak.Compute("Other()").ToString() }, ["data"] = "0x" },
        ];

        JsonElement result = await Success("decode_logs", ("logs", logs), ("abi", new[] { "event Ping(address indexed who, uint256 x)" }));

        JsonElement[] decoded = [.. result.GetProperty("logs").EnumerateArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("total").GetInt32(), Is.EqualTo(2));
            Assert.That(decoded[0].GetProperty("event").GetString(), Is.EqualTo("Ping"));
            Assert.That(decoded[0].GetProperty("source").GetString(), Is.EqualTo("abi"));
            Assert.That(decoded[0].GetProperty("params")[1].GetProperty("value").GetString(), Is.EqualTo("42"));
            Assert.That(decoded[1].GetProperty("decoded").GetBoolean(), Is.False);
            Assert.That(decoded[1].GetProperty("topics").GetArrayLength(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Decode_logs_rejects_invalid_input_and_unknown_transactions()
    {
        McpAssert.Error(await Call(_client, "decode_logs"), McpAssert.InvalidInput);
        McpAssert.Error(await Call(_client, "decode_logs", ("txHash", Keccak.Compute("x").ToString()), ("logs", Array.Empty<object>())), McpAssert.InvalidInput);
        McpAssert.Error(await Call(_client, "decode_logs", ("logs", Array.Empty<object>()), ("abi", new[] { "function f()" })), McpAssert.InvalidInput);
        McpAssert.Error(await Call(_client, "decode_logs", ("logs", new object[] { new Dictionary<string, object> { ["address"] = "0x1" } })), McpAssert.InvalidInput);
        McpAssert.Error(await Call(_client, "decode_logs", ("txHash", Keccak.Compute("unknown").ToString())), McpAssert.NotFound);
    }

    [Test]
    public async Task Resolve_ens_reads_the_registry_and_resolver()
    {
        JsonElement result = await Success("resolve_ens", ("name", "Alice.ETH"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("name").GetString(), Is.EqualTo("alice.eth"));
            Assert.That(result.GetProperty("node").GetString(), Is.EqualTo(McpEns.NameHash("alice.eth").ToString()));
            Assert.That(result.GetProperty("owner").GetString(), Is.EqualTo(TestItem.AddressA.ToString(true, true)));
            Assert.That(result.GetProperty("resolver").GetString(), Is.EqualTo(EnsResolver.ToString(true, true)));
            Assert.That(result.GetProperty("address").GetString(), Is.EqualTo(EnsTarget.ToString(true, true)));
            Assert.That(result.GetProperty("resolvedVia").GetString(), Is.EqualTo("resolver"));
        }
    }

    [Test]
    public async Task Resolve_ens_reports_unregistered_names_invalid_names_and_chains_without_ens()
    {
        JsonElement unregistered = await Success("resolve_ens", ("name", "nobody.eth"));
        Assert.That(unregistered.GetProperty("address").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(unregistered.GetProperty("message").GetString(), Does.Contain("not registered"));

        McpAssert.Error(await Call(_client, "resolve_ens", ("name", "bad..eth")), McpAssert.InvalidInput);
        JsonElement error = McpAssert.Error(await Call(_gnosisClient, "resolve_ens", ("name", "alice.eth")), McpAssert.Unavailable);
        Assert.That(error.GetProperty("message").GetString(), Does.Contain("ENS is not deployed"));
    }

    [Test]
    public async Task Lookup_address_profiles_an_eoa_with_a_verified_ens_name()
    {
        JsonElement result = await Success("lookup_address", ("address", Hex(EnsTarget)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("kind").GetString(), Is.EqualTo("eoa"));
            Assert.That(result.GetProperty("symbol").GetString(), Is.EqualTo("ETH"));
            Assert.That(result.GetProperty("codeSize").GetInt32(), Is.Zero);
            Assert.That(result.GetProperty("ens").GetProperty("name").GetString(), Is.EqualTo("alice.eth"));
            Assert.That(result.GetProperty("ens").GetProperty("verified").GetBoolean(), Is.True);
        }
    }

    [Test]
    public async Task Lookup_address_profiles_contracts_precompiles_and_empty_accounts()
    {
        JsonElement token = await Success("lookup_address", ("address", Hex(_deployed.ProxyToken)));
        JsonElement precompile = await Success("lookup_address", ("address", "0x0000000000000000000000000000000000000001"));
        JsonElement empty = await Success("lookup_address", ("address", "0x00000000000000000000000000000000deadbeef"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(token.GetProperty("kind").GetString(), Is.EqualTo("contract"));
            Assert.That(token.GetProperty("token").GetProperty("symbol").GetString(), Is.EqualTo("TST"));
            Assert.That(token.GetProperty("proxy").GetProperty("implementation").GetString(), Is.EqualTo(Implementation.ToString(true, true)));
            Assert.That(token.TryGetProperty("ens", out _), Is.False);
            Assert.That(precompile.GetProperty("kind").GetString(), Is.EqualTo("precompile"));
            Assert.That(empty.GetProperty("kind").GetString(), Is.EqualTo("empty"));
        }

        McpAssert.Error(await Call(_client, "lookup_address", ("address", "vitalik.eth")), McpAssert.InvalidInput);
    }

    [Test]
    public async Task Lookup_address_uses_the_forks_precompile_set()
    {
        await using McpTestNode osaka = await McpTestNode.Create(configureContainer: static b => b.AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance)));
        await using McpClient client = await osaka.CreateClient();

        JsonElement p256 = McpAssert.Success(await Call(client, "lookup_address", ("address", "0x0000000000000000000000000000000000000100")));

        Assert.That(p256.GetProperty("kind").GetString(), Is.EqualTo("precompile"), "P256VERIFY is a precompile from Osaka");
    }

    private async Task<JsonElement> Success(string tool, params (string Name, object? Value)[] arguments)
    {
        CallToolResult result = await Call(_client, tool, arguments);
        JsonElement value = McpAssert.Success(result);

        await McpToolCalls.AssertConformsToOutputSchema(_client, tool, result);
        return value;
    }

    private static Task<CallToolResult> Call(McpClient client, string tool, params (string Name, object? Value)[] arguments) =>
        McpToolCalls.Call(client, tool, arguments);

    private static string Hex(Address address) => address.ToString();

    private static string Topic(Address address) => "0x" + new string('0', 24) + address.ToString(false, false);

    private static async Task<Deployed> Deploy(McpTestNode node)
    {
        PrivateKey sender = TestItem.PrivateKeyB;
        ulong nonce = node.Chain.WorldStateManager.GlobalStateReader.GetNonce(node.Chain.BlockTree.Head!.Header, sender.Address);
        Transaction[] transactions =
        [
            DeployTx(nonce, TestContracts.TokenInit(TestContracts.Token("Test Token", "TST", bytes32Symbol: false, erc721: false), Holder, Supply)),
            DeployTx(nonce + 1, TestContracts.TokenInit(TestContracts.Token("Maker", "MKR", bytes32Symbol: true, erc721: false), Holder, Supply)),
            DeployTx(nonce + 2, TestContracts.TokenInit(TestContracts.Token("Test NFT", "NFT", bytes32Symbol: false, erc721: true), Holder, 1)),
            DeployTx(nonce + 3, TestContracts.TokenInit(TestContracts.Token("Test Token", "TST", bytes32Symbol: false, erc721: false), Holder, Supply, Implementation)),
            DeployTx(nonce + 4, TestContracts.TokenInit(TestContracts.Token("USD Coin", "USDC", bytes32Symbol: false, erc721: false), Holder, Supply, Implementation, ZosImplementationSlot)),
            DeployTx(nonce + 5, TestContracts.TokenInit(TestContracts.Token("Getter", "GET", bytes32Symbol: false, erc721: false, implementationGetter: Implementation), Holder, Supply)),
            DeployTx(nonce + 6, TestContracts.TokenInit(TestContracts.Token(EvilName, "EVIL", bytes32Symbol: false, erc721: false), Holder, Supply)),
        ];

        Block block = await node.Chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length), "precondition: every deployment must be mined");
        return new Deployed(
            ContractAddress.From(sender.Address, nonce),
            ContractAddress.From(sender.Address, nonce + 1),
            ContractAddress.From(sender.Address, nonce + 2),
            ContractAddress.From(sender.Address, nonce + 3),
            ContractAddress.From(sender.Address, nonce + 4),
            ContractAddress.From(sender.Address, nonce + 5),
            ContractAddress.From(sender.Address, nonce + 6),
            transactions[0]);

        Transaction DeployTx(ulong txNonce, byte[] initCode) =>
            Build.A.Transaction.WithChainId(node.Chain.SpecProvider.ChainId).WithNonce(txNonce).WithGasPrice(1).WithCode(initCode).WithGasLimit(800_000)
                .SignedAndResolved(node.Chain.EthereumEcdsa, sender).TestObject;
    }

    private sealed record Deployed(Address Token, Address Bytes32Token, Address NftToken, Address ProxyToken, Address ZosProxyToken, Address GetterProxyToken,
        Address EvilToken, Transaction TokenDeploy);

    /// <summary>Puts a minimal ENS registry and resolver in genesis: alice.eth resolves to <see cref="EnsTarget"/>, whose reverse record is alice.eth.</summary>
    private sealed class EnsGenesis(IWorldState state, ISpecProvider specProvider) : IGenesisPostProcessor
    {
        public void PostProcess(Block genesis)
        {
            IReleaseSpec spec = specProvider.GenesisSpec;
            Hash256 alice = McpEns.NameHash("alice.eth");
            Hash256 reverse = McpEns.NameHash(McpEns.ReverseName(EnsTarget));

            state.CreateAccount(EnsRegistry, UInt256.Zero);
            state.InsertCode(EnsRegistry, TestContracts.Registry(), spec, isGenesis: true);
            state.Set(new StorageCell(EnsRegistry, Slot(alice)), Value(TestItem.AddressA));
            state.Set(new StorageCell(EnsRegistry, Slot(alice) + UInt256.One), Value(EnsResolver));
            state.Set(new StorageCell(EnsRegistry, Slot(reverse) + UInt256.One), Value(EnsResolver));

            state.CreateAccount(EnsResolver, UInt256.Zero);
            state.InsertCode(EnsResolver, TestContracts.Resolver("alice.eth"), spec, isGenesis: true);
            state.Set(new StorageCell(EnsResolver, Slot(alice)), Value(EnsTarget));
        }

        private static UInt256 Slot(Hash256 node) => new(node.Bytes, isBigEndian: true);

        private static UInt256 Value(Address address)
        {
            byte[] word = new byte[32];
            address.Bytes.CopyTo(word.AsSpan(12));
            return new UInt256(word, isBigEndian: true);
        }
    }
}

/// <summary>Hand-assembled test contracts.</summary>
internal static class TestContracts
{
    public static readonly Hash256 TransferTopic = Keccak.Compute("Transfer(address,address,uint256)");

    /// <summary>
    /// An ERC-20-like runtime: name/symbol (ABI string, or bytes32 symbol), decimals 6, totalSupply, balanceOf (storage slot = holder),
    /// getReserves returning (1000, 2000, 3), fail() reverting with Error("nope"), boom() reverting with Panic(0x11), optional ERC-165/ERC-721
    /// supportsInterface, and an empty revert for anything else.
    /// </summary>
    public static byte[] Token(string name, string symbol, bool bytes32Symbol, bool erc721, Address? implementationGetter = null)
    {
        EvmAssembler asm = new EvmAssembler().Selector()
            .JumpIfSelector("name()", "name")
            .JumpIfSelector("symbol()", "symbol")
            .JumpIfSelector("decimals()", "decimals")
            .JumpIfSelector("totalSupply()", "totalSupply")
            .JumpIfSelector("balanceOf(address)", "balanceOf")
            .JumpIfSelector("getReserves()", "getReserves")
            .JumpIfSelector("fail()", "fail")
            .JumpIfSelector("boom()", "boom");
        if (erc721) asm.JumpIfSelector("supportsInterface(bytes4)", "supportsInterface");
        if (implementationGetter is not null) asm.JumpIfSelector("implementation()", "implementation");
        asm.Push(0).Push(0).Op(Instruction.REVERT);

        asm.Label("name").ReturnBlob(AbiString(name));
        asm.Label("symbol").ReturnBlob(bytes32Symbol ? Bytes32(symbol) : AbiString(symbol));
        asm.Label("decimals").ReturnBlob(Word(6));
        asm.Label("totalSupply").Push(0).Op(Instruction.SLOAD).ReturnTop();
        asm.Label("balanceOf").Push(4).Op(Instruction.CALLDATALOAD).Op(Instruction.SLOAD).ReturnTop();
        asm.Label("getReserves").ReturnBlob([.. Word(1000), .. Word(2000), .. Word(3)]);
        asm.Label("fail").RevertBlob([.. Bytes.FromHexString("0x08c379a0"), .. AbiString("nope")]);
        asm.Label("boom").RevertBlob([.. Bytes.FromHexString("0x4e487b71"), .. Word(0x11)]);
        if (implementationGetter is not null)
        {
            byte[] word = new byte[32];
            implementationGetter.Bytes.CopyTo(word.AsSpan(12));
            asm.Label("implementation").ReturnBlob(word);
        }

        if (erc721)
        {
            asm.Label("supportsInterface").Push(4).Op(Instruction.CALLDATALOAD)
                .Op(Instruction.DUP1).PushWord(LeftAligned("0x01ffc9a7")).Op(Instruction.EQ)
                .Op(Instruction.SWAP1).PushWord(LeftAligned("0x80ac58cd")).Op(Instruction.EQ)
                .Op(Instruction.OR).ReturnTop();
        }

        return asm.Build();
    }

    /// <summary>Init code: stores the supply in slot 0 and the holder's balance, emits a mint Transfer, optionally sets a proxy implementation slot (EIP-1967 by default).</summary>
    public static byte[] TokenInit(byte[] runtime, Address holder, UInt256 supply, Address? implementation = null,
        string implementationSlot = "0x360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc")
    {
        byte[] holderTopic = new byte[32];
        holder.Bytes.CopyTo(holderTopic.AsSpan(12));
        Prepare init = Prepare.EvmCode
            .PushData(supply).PushData(0).Op(Instruction.SSTORE)
            .PushData(supply).PushData(holder).Op(Instruction.SSTORE)
            .PushData(supply).PushData(0).Op(Instruction.MSTORE)
            // LOGn pops topics last-pushed first: this emits [Transfer, from = 0, to = holder].
            .Log(32, 0, [new Hash256(holderTopic), Keccak.Zero, TransferTopic]);
        if (implementation is not null)
        {
            init.PushData(implementation).PushData(implementationSlot).Op(Instruction.SSTORE);
        }

        return init.ForInitOf(runtime).Done;
    }

    /// <summary>A registry answering owner(node) from slot node and resolver(node) from slot node + 1.</summary>
    public static byte[] Registry() => new EvmAssembler().Selector()
        .JumpIfSelector("owner(bytes32)", "owner")
        .JumpIfSelector("resolver(bytes32)", "resolver")
        .Push(0).Push(0).Op(Instruction.REVERT)
        .Label("owner").Push(4).Op(Instruction.CALLDATALOAD).Op(Instruction.SLOAD).ReturnTop()
        .Label("resolver").Push(1).Push(4).Op(Instruction.CALLDATALOAD).Op(Instruction.ADD).Op(Instruction.SLOAD).ReturnTop()
        .Build();

    /// <summary>A resolver answering addr(node) from slot node and name(node) with a fixed name.</summary>
    public static byte[] Resolver(string name) => new EvmAssembler().Selector()
        .JumpIfSelector("addr(bytes32)", "addr")
        .JumpIfSelector("name(bytes32)", "name")
        .Push(0).Push(0).Op(Instruction.REVERT)
        .Label("addr").Push(4).Op(Instruction.CALLDATALOAD).Op(Instruction.SLOAD).ReturnTop()
        .Label("name").ReturnBlob(AbiString(name))
        .Build();

    public static byte[] AbiString(string text) =>
        McpAbiCodec.TryEncode([new McpAbiParam(string.Empty, McpAbiType.String)], [JsonSerializer.SerializeToElement(text)], 4096, out byte[]? data, out _) ? data : [];

    private static byte[] Bytes32(string text)
    {
        byte[] word = new byte[32];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(word, 0);
        return word;
    }

    private static byte[] Word(ulong value)
    {
        byte[] word = new byte[32];
        ((UInt256)value).ToBigEndian(word);
        return word;
    }

    private static byte[] LeftAligned(string selector)
    {
        byte[] word = new byte[32];
        Bytes.FromHexString(selector).CopyTo(word, 0);
        return word;
    }

    /// <summary>A tiny two-pass EVM assembler with labels, enough for selector dispatchers.</summary>
    private sealed class EvmAssembler
    {
        private readonly List<byte> _code = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int Position, string Label)> _fixups = [];

        public EvmAssembler Op(Instruction instruction)
        {
            _code.Add((byte)instruction);
            return this;
        }

        public EvmAssembler Push(ulong value)
        {
            byte[] word = Word(value);
            int start = 0;
            while (start < 31 && word[start] == 0) start++;
            return PushBytes(word.AsSpan(start));
        }

        public EvmAssembler PushWord(byte[] word) => PushBytes(word);

        public EvmAssembler Selector() => Push(0).Op(Instruction.CALLDATALOAD).Push(0xe0).Op(Instruction.SHR);

        public EvmAssembler JumpIfSelector(string signature, string label)
        {
            Op(Instruction.DUP1).PushBytes(McpAbiSignature.Parse(signature).Selector).Op(Instruction.EQ);
            _code.Add((byte)Instruction.PUSH2);
            _fixups.Add((_code.Count, label));
            _code.Add(0);
            _code.Add(0);
            return Op(Instruction.JUMPI);
        }

        public EvmAssembler Label(string label)
        {
            _labels[label] = _code.Count;
            return Op(Instruction.JUMPDEST);
        }

        public EvmAssembler ReturnTop() => Push(0).Op(Instruction.MSTORE).Push(32).Push(0).Op(Instruction.RETURN);

        public EvmAssembler ReturnBlob(byte[] blob) => StoreBlob(blob).Push((ulong)blob.Length).Push(0).Op(Instruction.RETURN);

        public EvmAssembler RevertBlob(byte[] blob) => StoreBlob(blob).Push((ulong)blob.Length).Push(0).Op(Instruction.REVERT);

        public byte[] Build()
        {
            foreach ((int position, string label) in _fixups)
            {
                int target = _labels[label];
                _code[position] = (byte)(target >> 8);
                _code[position + 1] = (byte)target;
            }

            return [.. _code];
        }

        private EvmAssembler StoreBlob(byte[] blob)
        {
            for (int offset = 0; offset < blob.Length; offset += 32)
            {
                byte[] chunk = new byte[32];
                blob.AsSpan(offset, Math.Min(32, blob.Length - offset)).CopyTo(chunk);
                PushBytes(chunk).Push((ulong)offset).Op(Instruction.MSTORE);
            }

            return this;
        }

        private EvmAssembler PushBytes(ReadOnlySpan<byte> bytes)
        {
            _code.Add((byte)(Instruction.PUSH1 + (byte)(bytes.Length - 1)));
            _code.AddRange(bytes);
            return this;
        }
    }
}
