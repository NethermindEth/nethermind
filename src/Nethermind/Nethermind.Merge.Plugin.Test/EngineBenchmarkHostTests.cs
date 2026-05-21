// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Benchmark;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
public class EngineBenchmarkHostTests
{
    [Test]
    public async Task Json_server_writes_single_and_batch_responses()
    {
        IEngineRpcModule engineModule = Substitute.For<IEngineRpcModule>();
        engineModule
            .engine_exchangeCapabilities(Arg.Any<IEnumerable<string>>())
            .Returns(ResultWrapper<IReadOnlyList<string>>.Success(["engine_getPayloadV1"]));

        using IHost host = EngineBenchmarkHost.BuildJsonServer(engineModule);
        using HttpClient client = host.GetTestClient();

        JsonElement single = await PostJsonRpcAsync(client, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_exchangeCapabilities\",\"params\":[[]]}");
        Assert.That(single.GetProperty("result")[0].GetString(), Is.EqualTo("engine_getPayloadV1"));

        JsonElement batch = await PostJsonRpcAsync(client, "[{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"engine_exchangeCapabilities\",\"params\":[[]]},{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"engine_exchangeCapabilities\",\"params\":[[]]}]");
        Assert.That(batch.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(batch.GetArrayLength(), Is.EqualTo(2));
        Assert.That(batch[0].GetProperty("id").GetInt64(), Is.EqualTo(2));
        Assert.That(batch[1].GetProperty("id").GetInt64(), Is.EqualTo(3));
    }

    private static async Task<JsonElement> PostJsonRpcAsync(HttpClient client, string body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = EngineBenchmarkHost.Authorization;

        using HttpResponseMessage response = await client.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody);

        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document.RootElement.Clone();
    }
}
