// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_fillTransaction_fills_nonce_gas_and_chainId_for_legacy()
    {
        using Context ctx = await Context.Create();

        // Caller provides gasPrice (forces legacy path), to and value. Nonce, gas, chainId omitted.
        TransactionForRpc rpcTx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"value\":\"0x1\",\"gasPrice\":\"0x10\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);
        JToken parsed = JToken.Parse(serialized);
        parsed["error"].Should().BeNull();

        JToken result = parsed["result"]!;
        result["raw"]!.Value<string>().Should().StartWith("0x");

        JToken tx = result["tx"]!;
        tx["nonce"]!.Value<string>().Should().NotBeNullOrEmpty();
        tx["gas"]!.Value<string>().Should().NotBeNullOrEmpty();
        tx["chainId"]!.Value<string>().Should().NotBeNullOrEmpty();
        tx["from"]!.Value<string>()!.ToLowerInvariant().Should().Be(TestItem.AddressA.ToString());
    }

    [Test]
    public async Task Eth_fillTransaction_fills_eip1559_fees_when_max_fee_omitted()
    {
        using Context ctx = await Context.Create();

        TransactionForRpc rpcTx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"value\":\"0x1\",\"type\":\"0x2\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);
        JToken parsed = JToken.Parse(serialized);
        parsed["error"].Should().BeNull();

        JToken tx = parsed["result"]!["tx"]!;
        tx["maxFeePerGas"]!.Value<string>().Should().NotBeNullOrEmpty();
        tx["maxPriorityFeePerGas"]!.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Eth_fillTransaction_raw_rlp_round_trips()
    {
        using Context ctx = await Context.Create();

        TransactionForRpc rpcTx = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\":\"{TestItem.AddressA}\",\"to\":\"{TestItem.AddressB}\",\"value\":\"0x1\",\"gasPrice\":\"0x10\"}}");

        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);
        JToken result = JToken.Parse(serialized)["result"]!;
        string rawHex = result["raw"]!.Value<string>()!;
        byte[] raw = Nethermind.Core.Extensions.Bytes.FromHexString(rawHex);

        Transaction decoded = TxDecoder.Instance.DecodeCompleteNotNull(raw,
            RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping);
        decoded.To.Should().Be(TestItem.AddressB);
        decoded.Value.Should().Be((UInt256)1);
        decoded.GasPrice.Should().Be((UInt256)0x10);
    }
}
