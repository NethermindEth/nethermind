// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

    [TestCase("bad", null)]
    [TestCase("5s", 5)]
    [TestCase("00:00:05", 5)]
    [TestCase("\\u0035s", 5)]
    public void ByNumber_options_defer_duration_parsing(string timeout, int? seconds)
    {
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>($$"""{"timeout":"{{timeout}}","tracer":"noopTracer"}""", serializerOptions)!;
        GethTraceOptions copy = options with { Limit = 1 };

        Assert.That(options.Tracer, Is.EqualTo("noopTracer"));
        if (seconds is null)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.Timeout, Is.Null);
                Assert.That(copy.Timeout, Is.Null);
                Assert.DoesNotThrow(() => _ = options.ToString());
                Assert.DoesNotThrow(() => JsonSerializer.Serialize(options, EthereumJsonSerializer.JsonRpcRequestOptions));
                Assert.Throws<TraceDurationFormatException>(() => options.ResolveTimeout());
                Assert.Throws<TraceDurationFormatException>(() => copy.ResolveTimeout());
            }
        }
        else
        {
            TimeSpan expected = TimeSpan.FromSeconds(seconds.Value);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.ParsedTimeout.RawText, Is.Null);
                Assert.That(options.ParsedTimeout.Error, Is.Null);
                Assert.That(options.Timeout, Is.EqualTo(expected));
                Assert.That(options.ResolveTimeout(), Is.EqualTo(expected));
                Assert.That(copy.Timeout, Is.EqualTo(expected));
                Assert.That(copy.ResolveTimeout(), Is.EqualTo(expected));
            }
        }
    }

    [Test]
    public void Deferred_duration_bound_does_not_echo_oversized_input()
    {
        string timeout = new('x', 10_000);
        GethTraceOptions options = new() { ParsedTimeout = CustomTimeDurationConverter.Parse(timeout) };

        TraceDurationFormatException exception = Assert.Throws<TraceDurationFormatException>(() => options.ResolveTimeout())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Timeout, Is.Null);
            Assert.That(exception.Message, Does.Not.Contain(timeout));
            Assert.That(exception.Message, Does.Contain("64 bytes"));
        }
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
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        GethTraceOptions actual = JsonSerializer.Deserialize<GethTraceOptions>(json, serializerOptions)!;

        Assert.That(
            JsonSerializer.Serialize(actual, EthereumJsonSerializer.JsonRpcRequestOptions),
            Is.EqualTo(JsonSerializer.Serialize(expected, EthereumJsonSerializer.JsonRpcRequestOptions)));
    }

    [Test]
    public void ByNumber_options_use_the_supplied_metadata_resolver()
    {
        RecordingResolver resolver = new(EthereumJsonSerializer.JsonRpcRequestOptions.TypeInfoResolver!);
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions(resolver);

        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>("{\"timeout\":\"5s\"}", serializerOptions)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Timeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(resolver.RequestedTypes, Does.Contain("WireOptions"));
        }
    }

    [Test]
    public void ByNumber_options_reject_non_object_roots_with_json_exception([Values("17", "[]", "\"bad\"", "true")] string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GethTraceOptions>(json, CreateByNumberSerializerOptions()));

    [Test]
    public void ByNumber_options_preserve_reader_options()
    {
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        serializerOptions.AllowTrailingCommas = true;
        serializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
        serializerOptions.MaxDepth = 128;
        string nested = new string('[', 65) + "0," + new string(']', 65);

        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(
            $$"""{"tracerConfig":{"value":{{nested}},/* retained by the raw nested value */},}""", serializerOptions)!;

        Assert.That(options.TracerConfig, Is.Not.Null);
    }

    [Test]
    public void ByNumber_null_normalization_uses_custom_property_names()
    {
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        serializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;

        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(
            "{\"disable_stack\":true,\"disable_stack\":null}", serializerOptions)!;

        Assert.That(options.DisableStack, Is.True);
    }

    [Test]
    public void ByNumber_null_normalization_uses_supplied_property_metadata()
    {
        IJsonTypeInfoResolver resolver = EthereumJsonSerializer.JsonRpcRequestOptions.TypeInfoResolver!.WithAddedModifier(static typeInfo =>
        {
            if (typeInfo.Type != typeof(GethTraceOptionsConverter.WireOptions)) return;
            typeInfo.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            foreach (JsonPropertyInfo property in typeInfo.Properties)
                if (property.AttributeProvider is PropertyInfo { Name: nameof(GethTraceOptions.Limit) }) property.Name = "budget";
        });
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions(resolver);

        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>("{\"budget\":2,\"budget\":null}", serializerOptions)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Limit, Is.EqualTo(2));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GethTraceOptions>("{\"limit\":null}", serializerOptions));
        }
    }

    [Test]
    public void ByNumber_null_normalization_respects_case_sensitive_names()
    {
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        serializerOptions.PropertyNameCaseInsensitive = false;
        serializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GethTraceOptions>("{\"LIMIT\":null}", serializerOptions));
    }

    [Test]
    public void ByNumber_null_normalization_rejects_removed_property()
    {
        IJsonTypeInfoResolver resolver = EthereumJsonSerializer.JsonRpcRequestOptions.TypeInfoResolver!.WithAddedModifier(static typeInfo =>
        {
            if (typeInfo.Type != typeof(GethTraceOptionsConverter.WireOptions)) return;
            typeInfo.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo { Name: nameof(GethTraceOptions.EnableMemory) }) typeInfo.Properties.RemoveAt(i);
        });
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions(resolver);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GethTraceOptions>("{\"enableMemory\":null}", serializerOptions));
    }

    [TestCase("disableStack", "{}")]
    [TestCase("limit", "\"bad\"")]
    public void ByNumber_null_normalization_preserves_invalid_earlier_tokens(string property, string invalid) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GethTraceOptions>(BuildMembers(property, $"{invalid},null"), CreateByNumberSerializerOptions()));

    [TestCase(50_000_000L, "5s")]
    [TestCase(long.MaxValue, "256204778h48m5.4775807s")]
    [TestCase(long.MinValue, "-256204778h48m5.4775808s")]
    public void ByNumber_options_write_the_normal_duration_format(long ticks, string expected)
    {
        GethTraceOptions options = new() { Timeout = TimeSpan.FromTicks(ticks) };

        string json = JsonSerializer.Serialize(options, CreateByNumberSerializerOptions());
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.That(document.RootElement.GetProperty("timeout").GetString(), Is.EqualTo(expected));
    }

    [Test]
    public void ByNumber_options_round_trip_malformed_raw_duration()
    {
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>("{\"timeout\":\"bad\"}", serializerOptions)!;

        string json = JsonSerializer.Serialize(options, serializerOptions);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.That(document.RootElement.GetProperty("timeout").GetString(), Is.EqualTo("bad"));
    }

    [TestCase("{}", null)]
    [TestCase("""{"timeout":null}""", null)]
    [TestCase("""{"timeout":"bad","timeout":null}""", null)]
    [TestCase("""{"timeout":"bad","timeout":"5s"}""", 5)]
    [TestCase("""{"timeout":null,"timeout":"5s"}""", 5)]
    public void ByNumber_options_use_the_last_timeout_and_allow_explicit_overrides(string json, int? seconds)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(json, CreateByNumberSerializerOptions())!;
        GethTraceOptions overridden = options with { Timeout = TimeSpan.FromSeconds(7) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.ResolveTimeout(), Is.EqualTo(seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null));
            Assert.That(overridden.ResolveTimeout(), Is.EqualTo(TimeSpan.FromSeconds(7)));
            Assert.That(overridden.ParsedTimeout.Error, Is.Null);
            Assert.That(overridden.ParsedTimeout.RawText, Is.Null);
        }
    }

    [TestCase("""{"timeout":"5s","timeout":"bad"}""")]
    [TestCase("""{"timeout":null,"timeout":"bad"}""")]
    public void ByNumber_options_clear_malformed_timeout_on_explicit_override(string json)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(json, CreateByNumberSerializerOptions())!;
        Assert.That(options.ParsedTimeout.Error, Does.Contain("Cannot parse 'bad'"));

        GethTraceOptions overridden = options with { Timeout = null };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overridden.ResolveTimeout(), Is.Null);
            Assert.That(overridden.ParsedTimeout.RawText, Is.Null);
        }
    }

    private static IEnumerable<TestCaseData> NullBooleanCases()
    {
        foreach (string property in new[] { "disableStorage", "disableStack", "enableMemory", "enableReturnData" })
            foreach ((string values, bool expected) in new (string, bool)[]
            {
                ("null", false), ("true,null", true), ("null,true", true), ("true,false,null", false), ("\"true\",null", true),
                ("null,\"true\"", true)
            })
                yield return new TestCaseData(values, expected, property);
    }

    private static string BuildMembers(string property, string values)
    {
        string[] tokens = values.Split(',');
        string[] members = new string[tokens.Length];
        for (int i = 0; i < tokens.Length; i++) members[i] = $"\"{property}\":{tokens[i]}";
        return "{" + string.Join(',', members) + "}";
    }

    [TestCaseSource(nameof(NullBooleanCases))]
    public void ByNumber_null_boolean_does_not_replace_the_previous_value(string values, bool expected, string property)
    {
        JsonSerializerOptions serializerOptions = CreateByNumberSerializerOptions();
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(BuildMembers(property, values), serializerOptions)!;
        bool actual = property switch
        {
            "disableStorage" => options.DisableStorage,
            "disableStack" => options.DisableStack,
            "enableMemory" => options.EnableMemory,
            _ => options.EnableReturnData
        };
        GethTraceOptions copy = options with { DisableStorage = !expected, DisableStack = !expected, EnableMemory = !expected, EnableReturnData = !expected };
        using JsonDocument serialized = JsonDocument.Parse(JsonSerializer.Serialize(copy, serializerOptions));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(serialized.RootElement.GetProperty(property).GetBoolean(), Is.EqualTo(!expected), "record copies must serialize their current values");
        }
    }

    [TestCase("null", 0)]
    [TestCase("2,null", 2)]
    [TestCase("null,2", 2)]
    [TestCase("2,3,null", 3)]
    public void ByNumber_null_limit_does_not_replace_the_previous_value(string values, long expected)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(BuildMembers("limit", values), CreateByNumberSerializerOptions())!;

        Assert.That(options.Limit, Is.EqualTo(expected));
    }

    [TestCase("timeout", "{\"timeout\":\"5s\",\"timeout\":null}")]
    [TestCase("tracer", "{\"tracer\":\"noopTracer\",\"tracer\":null}")]
    [TestCase("txIndex", "{\"txIndex\":\"0x2\",\"txIndex\":null}")]
    [TestCase("streamMode", "{\"streamMode\":true,\"streamMode\":null}")]
    public void ByNumber_null_nullable_option_replaces_the_previous_value(string property, string json)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(json, CreateByNumberSerializerOptions())!;

        object? actual = property switch
        {
            "timeout" => options.Timeout,
            "tracer" => options.Tracer,
            "streamMode" => options.StreamMode,
            _ => options.TxIndex
        };
        Assert.That(actual, Is.Null);
    }

    [Test]
    public void ByNumber_boolean_normalization_preserves_raw_timeout_length_validation([Values(true, false)] bool includeNull)
    {
        string timeout = new string('0', 11).Replace("0", "\\u0030", StringComparison.Ordinal) + "1s";
        string json = includeNull
            ? $"{{\"disableStack\":null,\"timeout\":\"{timeout}\"}}"
            : $"{{\"timeout\":\"{timeout}\"}}";

        JsonException exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<GethTraceOptions>(json, CreateByNumberSerializerOptions()))!;

        Assert.That(exception.Message, Does.Contain("64 bytes"));
    }

    [TestCase("\"enableMemory\":true,\"disableMemory\":null", true)]
    [TestCase("\"disableMemory\":false,\"enableMemory\":null", true)]
    [TestCase("\"ENABLEMEMORY\":true,\"enableMemory\":null", true)]
    [TestCase("\"enableMemory\":true,\"disableMemory\":true,\"enableMemory\":null", false)]
    public void ByNumber_null_memory_alias_preserves_property_order(string members, bool expected)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>("{" + members + "}", CreateByNumberSerializerOptions())!;

        Assert.That(options.EnableMemory, Is.EqualTo(expected));
    }

    private static JsonSerializerOptions CreateByNumberSerializerOptions(IJsonTypeInfoResolver? resolver = null)
    {
        JsonSerializerOptions serializerOptions = new(EthereumJsonSerializer.JsonRpcRequestOptions);
        if (resolver is not null) serializerOptions.TypeInfoResolver = resolver;
        serializerOptions.Converters.Insert(0, new GethTraceOptionsConverter());
        return serializerOptions;
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
