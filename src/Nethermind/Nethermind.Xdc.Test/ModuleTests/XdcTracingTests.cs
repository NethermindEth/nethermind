// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Test;
using Nethermind.Network;
using Nethermind.Xdc.Test.Helpers;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

/// <summary>
/// The tracing environments must replay XDC blocks with <see cref="XdcBlockProcessor"/>: the generic processor turns the
/// header into a plain <see cref="BlockHeader"/>, which <see cref="XdcTransactionProcessor"/> cannot run a transaction on.
/// </summary>
[TestFixture]
internal class XdcTracingTests
{
    private const string Call = """{"from":"0x7e5f4552091a69125d5dfcb7b8c2659029395bdf","to":"0x2b5ad5c4795c026514f8317c7a215e218dccd6cf","gas":"0x5208","value":"0x0"}""";

    private XdcTestBlockchain _blockchain = null!;

    [SetUp]
    public async Task Setup() =>
        // The debug module needs a fork id, which the test chain spec cannot provide.
        _blockchain = await XdcTestBlockchain.Create(configurer: builder => builder.AddSingleton(Substitute.For<IForkInfo>()));

    [TearDown]
    public void TearDown() =>
        _blockchain?.Dispose();

    [Test]
    public async Task Call_is_traced([Values("trace_call", "trace_callMany", "debug_traceCall", "debug_traceCallMany")] string method)
    {
        string parameters = method switch
        {
            "trace_call" => $"""[{Call},["trace"],"latest"]""",
            "trace_callMany" => $"""[[[{Call},["trace"]]],"latest"]""",
            "debug_traceCall" => $"""[{Call},"latest"]""",
            _ => $$"""[[{"transactions":[{{Call}}]}],{"blockNumber":"latest"}]""",
        };

        string response = await Request(method, parameters);

        AssertTraced(method, response);
    }

    [Test]
    public async Task Transaction_is_traced([Values("trace_replayTransaction", "debug_traceTransaction")] string method)
    {
        Transaction transaction = _blockchain.CreateTransactionBuilder().To(TestItem.AddressC).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await _blockchain.AddBlock(transaction);
        Assert.That(block.Transactions, Has.Length.EqualTo(1));

        string hash = $"\"{transaction.Hash}\"";
        string response = await Request(method, method == "trace_replayTransaction" ? $"""[{hash},["trace"]]""" : $"[{hash}]");

        AssertTraced(method, response);
    }

    private Task<string> Request(string method, string parameters)
    {
        object?[] arguments = [.. JsonDocument.Parse(parameters).RootElement.Clone().EnumerateArray()];
        return method.StartsWith("trace_")
            ? RpcTest.TestSerializedRequest(CreateModule<ITraceRpcModule>(), method, arguments)
            : RpcTest.TestSerializedRequest(CreateModule<IDebugRpcModule>(), method, arguments);
    }

    private T CreateModule<T>() where T : IRpcModule =>
        _blockchain.Container.Resolve<IRpcModuleFactory<T>>().Create();

    private static void AssertTraced(string method, string response)
    {
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);

        JToken? result = json["result"];
        if (method.EndsWith("Many")) result = result?[0];
        if (method == "debug_traceCallMany") result = result?[0];

        using (Assert.EnterMultipleScope())
        {
            if (method.StartsWith("trace_"))
            {
                Assert.That((string?)result?["trace"]?[0]?["type"], Is.EqualTo("call"), response);
                Assert.That(result?["trace"]?[0]?["error"], Is.Null, response);
            }
            else
            {
                Assert.That((bool?)result?["failed"], Is.False, response);
                Assert.That(result?["error"], Is.Null, response);
            }
        }
    }
}
