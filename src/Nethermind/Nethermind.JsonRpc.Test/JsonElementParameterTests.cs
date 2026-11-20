// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.JsonRpc.Test;

public class JsonElementParameterTests
{
    [Test]
    public async Task Raw_json_parameters_preserve_the_original_value(
        [Values] bool utf8, [Values("test_value", "test_optionalValue")] string method,
        [Values("\"latest\"", "\"0xa\"", "5", "{\"blockHash\":\"0x1\"}")] string value)
    {
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Eth] };
        RpcModuleProvider provider = new(new RealFileSystem(), config, new EthereumJsonSerializer(), NullLogManager.Instance);
        provider.Register(new SingletonModulePool<IValueRpcModule>(new ValueRpcModule(), true));
        using GCKeeper keeper = new(NoGCStrategy.Instance, NullLogManager.Instance);
        JsonRpcService service = new(provider, NullLogManager.Instance, config, keeper);
        JsonRpcRequest request = new() { JsonRpc = "2.0", Id = new JsonRpcId(1L), Method = method };
        using JsonDocument parameters = JsonDocument.Parse("[" + value + "]");
        if (utf8)
        {
            request.ParamsUtf8 = Encoding.UTF8.GetBytes("[" + value + "]");
            request.ParamsKind = JsonValueKind.Array;
        }
        else request.Params = parameters.RootElement;

        using JsonRpcResponse response = await service.SendRequestAsync(request, new JsonRpcContext(RpcEndpoint.Http));

        Assert.That(response, Is.AssignableTo<IResultWrapper>());
        object? data = ((IResultWrapper)response).Data;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(data, Is.TypeOf<JsonElement>());
            Assert.That(((JsonElement)data!).GetRawText(), Is.EqualTo(value));
        }
    }

    [RpcModule(ModuleType.Eth)]
    private interface IValueRpcModule : IRpcModule
    {
        ResultWrapper<JsonElement> test_value(JsonElement value);
        ResultWrapper<JsonElement?> test_optionalValue(JsonElement? value = null);
    }

    private sealed class ValueRpcModule : IValueRpcModule
    {
        public ResultWrapper<JsonElement> test_value(JsonElement value) => ResultWrapper<JsonElement>.Success(value);
        public ResultWrapper<JsonElement?> test_optionalValue(JsonElement? value = null) => ResultWrapper<JsonElement?>.Success(value);
    }
}
