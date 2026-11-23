// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceTransaction_rejects_prestate_diff_with_include_empty_as_trace_error()
    {
        using Context context = await Context.Create();
        Block block = await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", block.Transactions[0].Hash!,
            new { tracer = "prestateTracer", tracerConfig = new { diffMode = true, includeEmpty = true } });

        JToken error = JToken.Parse(response)["error"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)error["code"], Is.EqualTo(ErrorCodes.InvalidInput), response);
            Assert.That((string?)error["message"], Is.EqualTo("cannot use diffMode with includeEmpty"), response);
        }
    }
}
