// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Facade.Eth.RpcTransaction;
using NUnit.Framework;
using FluentAssertions;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [TestCase("0xFFFFFFFF", "block not found: 0xffffffff")]
    [TestCase("0x123456", "block not found: 0x123456")]
    [TestCase(
        "0xf0b3f69cbd4e1e8d9b0ef02ff5d1384d18e19d251a4052f5f90bab190c5e8937",
        "block not found: 0xf0b3f69cbd4e1e8d9b0ef02ff5d1384d18e19d251a4052f5f90bab190c5e8937")]
    public async Task Eth_estimateGas_returns_geth_compatible_error_for_missing_block(string blockId, string expectedMessage)
    {
        using Context ctx = await Context.Create();

        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gasPrice":"0xBA43B7400"}""");

        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, blockId);

        JObject response = JObject.Parse(serialized);
        response.Should().ContainKey("error");
        response["error"]!["code"]!.Value<int>().Should().Be(-32000);
        response["error"]!["message"]!.Value<string>().Should().Be(expectedMessage);
    }
}
