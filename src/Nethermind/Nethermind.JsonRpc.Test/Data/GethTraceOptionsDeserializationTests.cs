// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

[Parallelizable(ParallelScope.All)]
public class GethTraceOptionsDeserializationTests
{
    [Test]
    public void Native_boolean_config_uses_supplied_serializer_options([Values] bool caseInsensitive)
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonRpcRequestOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = caseInsensitive
        };
        JsonElement json = JsonSerializer.Deserialize<JsonElement>("""
            {"ONLY_TOP_CALL":"true","only_top_call":null,"WITH_LOG":"true","with_log":null,
             "DIFF_MODE":"true","diff_mode":null,"DISABLE_CODE":"true","disable_code":null,
             "DISABLE_STORAGE":"true","disable_storage":null,"unknown":[]}
            """);

        NativeCallTracerConfig call = NativeCallTracerConfig.FromJson(json, options);
        NativePrestateTracerConfig prestate = NativePrestateTracerConfig.FromJson(json, options);
        NativeCallTracerConfig defaultCall = NativeCallTracerConfig.FromJson(null, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OnlyTopCall, Is.EqualTo(caseInsensitive));
            Assert.That(call.WithLog, Is.EqualTo(caseInsensitive));
            Assert.That(prestate.DiffMode, Is.EqualTo(caseInsensitive));
            Assert.That(prestate.DisableCode, Is.EqualTo(caseInsensitive));
            Assert.That(prestate.DisableStorage, Is.EqualTo(caseInsensitive));
            Assert.That(defaultCall.OnlyTopCall, Is.False);
            Assert.That(defaultCall.WithLog, Is.False);
            Assert.That(NativeCallTracerConfig.FromJson(null, options), Is.SameAs(defaultCall));
            Assert.That(NativeCallTracerConfig.FromJson(JsonSerializer.Deserialize<JsonElement>("null"), options), Is.SameAs(defaultCall));
            Assert.That(NativeCallTracerConfig.FromJson(JsonSerializer.Deserialize<JsonElement>("{}"), options), Is.Not.SameAs(defaultCall));
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
