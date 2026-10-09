// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

[Parallelizable(ParallelScope.All)]
public class GethTraceOptionsDeserializationTests
{
    [TestCase("bad", null)]
    [TestCase("5s", 5)]
    public void ByNumber_options_defer_duration_parsing(string timeout, int? seconds)
    {
        JsonSerializerOptions serializerOptions = new(EthereumJsonSerializer.JsonRpcRequestOptions);
        serializerOptions.Converters.Insert(0, new GethTraceOptionsConverter());
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>($$"""{"timeout":"{{timeout}}","tracer":"noopTracer"}""", serializerOptions)!;

        Assert.That(options.Tracer, Is.EqualTo("noopTracer"));
        if (seconds is null) Assert.Catch<FormatException>(() => _ = options.Timeout);
        else Assert.That(options.Timeout, Is.EqualTo(TimeSpan.FromSeconds(seconds.Value)));
    }

    [Test]
    public void Deferred_duration_bound_does_not_echo_oversized_input()
    {
        string timeout = new('x', 10_000);
        GethTraceOptions options = new() { TimeoutText = timeout };

        FormatException exception = Assert.Catch<FormatException>(() => _ = options.Timeout)!;

        Assert.That(exception.Message, Does.Not.Contain(timeout));
    }

    [Test]
    public void ByNumber_options_preserve_the_existing_wire_contract()
    {
        const string json = """
            {
              "disableMemory":false,"disableStorage":true,"enableMemory":true,"enableReturnData":true,"disableStack":true,
              "limit":123,"timeout":"5s","tracer":"noopTracer","txHash":"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "txIndex":"0x2","tracerConfig":{"onlyTopCall":true},
              "stateOverrides":{"0x0000000000000000000000000000000000000001":{"balance":"0x2"}},
              "blockOverrides":{"number":"0x3"},"streamMode":false
            }
            """;
        GethTraceOptions expected = JsonSerializer.Deserialize<GethTraceOptions>(json, EthereumJsonSerializer.JsonRpcRequestOptions)!;
        JsonSerializerOptions serializerOptions = new(EthereumJsonSerializer.JsonRpcRequestOptions);
        serializerOptions.Converters.Insert(0, new GethTraceOptionsConverter());
        GethTraceOptions actual = JsonSerializer.Deserialize<GethTraceOptions>(json, serializerOptions)!;

        Assert.That(
            JsonSerializer.Serialize(actual, EthereumJsonSerializer.JsonRpcRequestOptions),
            Is.EqualTo(JsonSerializer.Serialize(expected, EthereumJsonSerializer.JsonRpcRequestOptions)));
    }

    [Test]
    public void ByNumber_options_use_the_supplied_metadata_resolver()
    {
        RecordingResolver resolver = new(EthereumJsonSerializer.JsonRpcRequestOptions.TypeInfoResolver!);
        JsonSerializerOptions serializerOptions = new(EthereumJsonSerializer.JsonRpcRequestOptions)
        {
            TypeInfoResolver = resolver
        };
        serializerOptions.Converters.Insert(0, new GethTraceOptionsConverter());

        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>("{\"timeout\":\"5s\"}", serializerOptions)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Timeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(resolver.RequestedTypes, Does.Contain("WireOptions"));
        }
    }

    private sealed class RecordingResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
    {
        internal HashSet<string> RequestedTypes { get; } = [];

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            RequestedTypes.Add(type.Name);
            return inner.GetTypeInfo(type, options);
        }
    }

    // The node reads RPC parameters through source-generated metadata, which assigns every init-only member;
    // the legacy disableMemory alias must decide memory capture as reflection did: the last of the two in the JSON wins.
    [TestCase("{}", false)]
    [TestCase("""{"disableMemory":false}""", true)]
    [TestCase("""{"disableMemory":true}""", false)]
    [TestCase("""{"enableMemory":true}""", true)]
    [TestCase("""{"enableMemory":false}""", false)]
    [TestCase("""{"disableMemory":true,"enableMemory":true}""", true)]
    [TestCase("""{"enableMemory":true,"disableMemory":true}""", false)]
    public void Memory_capture_follows_the_options_present(string json, bool enableMemory)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(json, EthereumJsonSerializer.JsonRpcRequestOptions)!;

        Assert.That(options.EnableMemory, Is.EqualTo(enableMemory));
    }
}
