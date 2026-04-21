// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using NUnit.Framework;
using FluentAssertions;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Eth_estimateGas_returns_geth_compatible_error_for_missing_block()
    {
        using Context ctx = await Context.Create();
        
        TransactionForRpc transaction = ctx.Test.JsonSerializer.Deserialize<TransactionForRpc>(
            """{"from":"0xa9Ac1233699BDae25abeBae4f9Fb54DbB1b44700","to":"0x252568abdeb9de59fd8963dfcd87be2db65f1ce1","gasPrice":"0xBA43B7400"}""");
        
        string serialized = await ctx.Test.TestEthRpc("eth_estimateGas", transaction, "0xFFFFFFFF");
        
        JToken response = JToken.Parse(serialized);
        response["error"]!["code"]!.Value<int>().Should().Be(-32000);
        response["error"]!["message"]!.Value<string>().Should().Be("block not found: 0xffffffff");
    }
}
