// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private const string HeldAccount = "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf";
    private const string ForeignAccount = "0x000000000000000000000000000000000000dead";
    private const string FeeRecipient = "0x2d44c0e097f6cd0f514edac633d82e01280b4a5c";
    private const string SetCodeAuthorization = """[{"chainId":"0x1","address":"0x0000000000000000000000000000000000000001","nonce":"0x0","yParity":"0x0","r":"0x1","s":"0x1"}]""";

    [TestCase("eth_sendTransaction", TestName = "Send")]
    [TestCase("eth_signTransaction", TestName = "Sign")]
    [TestCase("eth_fillTransaction", TestName = "Fill")]
    public async Task Fee_cap_below_the_priority_fee_is_reported_in_hexadecimal(string method) =>
        Assert.That(await FeeDefaultsError(method, """{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", london: true),
            Is.EqualTo("maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)"));

    [TestCase("eth_sendTransaction", """{"type":"0x2","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x0"}""", "maxFeePerGas must be non-zero", TestName = "Send, zero fee cap")]
    [TestCase("eth_signTransaction", """{"type":"0x2","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x3b9aca00"}""", "maxFeePerGas must be non-zero", TestName = "Sign, zero fee cap below the priority fee")]
    [TestCase("eth_fillTransaction", """{"type":"0x2","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x0"}""", "maxFeePerGas must be non-zero", TestName = "Fill, zero fee cap")]
    [TestCase("eth_sendTransaction", """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Send, zero gas price")]
    [TestCase("eth_signTransaction", """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Sign, zero gas price")]
    [TestCase("eth_fillTransaction", """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Fill, zero gas price")]
    [TestCase("eth_signTransaction", """{"type":"0x4","gasPrice":"0x1","authorizationList":AUTH}""", "both gasPrice and authorizationList specified", TestName = "Sign, gas price with an authorization list")]
    [TestCase("eth_fillTransaction", """{"type":"0x4","gasPrice":"0x1","authorizationList":AUTH}""", "both gasPrice and authorizationList specified", TestName = "Fill, gas price with an authorization list")]
    public async Task Fee_default_rules_reject_the_request(string method, string feeFields, string expected) =>
        Assert.That(await FeeDefaultsError(method, feeFields.Replace("AUTH", SetCodeAuthorization), london: true), Is.EqualTo(expected));

    [TestCase("eth_sendTransaction", TestName = "Send")]
    [TestCase("eth_fillTransaction", TestName = "Fill")]
    public async Task Fee_fields_before_london_are_rejected(string method) =>
        Assert.That(await FeeDefaultsError(method, """{"type":"0x2","maxPriorityFeePerGas":"0x3b9aca00"}""", london: false),
            Is.EqualTo("maxFeePerGas and maxPriorityFeePerGas are not valid before London is active"));

    [Test]
    public async Task Fill_fee_cap_below_the_suggested_priority_fee_is_reported_in_hexadecimal()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        string suggested = JToken.Parse(await ctx.Test.TestEthRpc("eth_maxPriorityFeePerGas"))["result"]!.Value<string>()!;
        Assume.That(suggested, Is.Not.EqualTo("0x0"), "a fee cap of 0 must sit below the suggested priority fee");

        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", FeeDefaultsRequest("""{"type":"0x2","maxFeePerGas":"0x0"}""", HeldAccount));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo($"maxFeePerGas (0x0) < maxPriorityFeePerGas ({suggested})"), serialized);
    }

    [Test]
    public async Task Send_from_an_account_this_node_does_not_hold_keeps_its_report()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string serialized = await ctx.Test.TestEthRpc("eth_sendTransaction",
            FeeDefaultsRequest("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", ForeignAccount));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("maxFeePerGas (10) < maxPriorityFeePerGas (1000000000)"), serialized);
    }

    private async Task<string?> FeeDefaultsError(string method, string feeFields, bool london)
    {
        using Context ctx = london ? await Context.CreateWithLondonEnabled() : await Context.Create();
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;

        string serialized = await ctx.Test.TestEthRpc(method, FeeDefaultsRequest(feeFields, HeldAccount));

        return JToken.Parse(serialized)["error"]?["message"]?.Value<string>();
    }

    private static object? FeeDefaultsRequest(string feeFields, string from)
    {
        JsonObject request = JsonNode.Parse(feeFields)!.AsObject();
        request["from"] = from;
        request["to"] = FeeRecipient;
        request["gas"] = "0x76c0";
        request["nonce"] = "0x0";
        return JsonSerializer.Deserialize<object>(request.ToJsonString());
    }
}
