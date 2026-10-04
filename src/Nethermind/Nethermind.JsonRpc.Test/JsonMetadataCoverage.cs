// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

/// <summary>
/// Fails the JSON-RPC test that serializes a type no source-generated context covers.
/// </summary>
/// <remarks>
/// Native AOT has no reflection-based metadata, so such a type fails to serialize there. A collection whose runtime type is
/// uncovered still serializes through a covered collection interface it implements, so only those without one are reported.
/// </remarks>
[SetUpFixture]
public class JsonMetadataCoverage
{
    private static readonly ConcurrentDictionary<Type, string> Uncovered = new();

    private sealed class Recorder : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (!IsTestOwned(type) && !IsClearScript(type) && !HasCoveredCollectionInterface(type, options))
            {
                string stackTrace = Environment.StackTrace;
                // The serializer asks about every ancestor of a polymorphic value while looking for a declared base; absence is the answer it expects.
                if (!stackTrace.Contains("FindNearestPolymorphicBaseType", StringComparison.Ordinal) && !TestOnly.ContainsKey(type) && !IsRequestedByTest(stackTrace))
                {
                    Uncovered.TryAdd(type, stackTrace);
                    throw new InvalidOperationException($"{type} has no source-generated JSON metadata; add it to the JSON context of the assembly that owns it.");
                }
            }

            return null;
        }
    }

    [OneTimeSetUp]
    public void Setup() => EthereumJsonSerializer.AddTypeInfoResolver(new Recorder(), (JsonTypeInfoResolverPriority)int.MaxValue);

    [OneTimeTearDown]
    public void TearDown()
    {
        string? path = Environment.GetEnvironmentVariable("JSON_COVERAGE_OUT");
        if (path is not null)
        {
            File.WriteAllLines(path, Uncovered.Select(static kv => $"{kv.Key}{Environment.NewLine}{kv.Value}{Environment.NewLine}----"));
        }
    }

    /// <summary>Uncovered types only test code asks for, with the reason no production path needs them.</summary>
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
        // Parameter types of a test RPC module; no production RPC method takes them.
        [typeof(JsonRpcContext)] = "test module parameter",
        [typeof(JsonRpcUrl)] = "test module parameter",
        [typeof(IJsonRpcDuplexClient)] = "test module parameter",
        [typeof(Nethermind.JsonRpc.Modules.RpcEndpoint)] = "test module parameter",
    };

    /// <remarks>A request whose first non-serializer frame is test code serves that test, not the node.</remarks>
    private static bool IsRequestedByTest(string stackTrace)
    {
        foreach (string frame in stackTrace.Split('\n'))
        {
            int start = frame.IndexOf("at Nethermind.", StringComparison.Ordinal);
            if (start < 0 || frame.Contains(nameof(JsonMetadataCoverage), StringComparison.Ordinal) ||
                frame.Contains("Nethermind.Serialization.Json.", StringComparison.Ordinal))
            {
                continue;
            }

            return frame.AsSpan(start).StartsWith("at Nethermind.JsonRpc.Test.", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsTestOwned(Type type) =>
        type.Assembly.GetName().Name!.EndsWith(".Test", StringComparison.Ordinal) ||
        type.IsGenericType && type.GetGenericArguments().Any(IsTestOwned) ||
        type.IsArray && IsTestOwned(type.GetElementType()!);

    private static bool IsClearScript(Type type) => type.Assembly.GetName().Name!.StartsWith("ClearScript", StringComparison.Ordinal);

    private static bool HasCoveredCollectionInterface(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        IEnumerable<IJsonTypeInfoResolver> contexts = options.TypeInfoResolverChain.OfType<JsonSerializerContext>();
        return type.GetInterfaces()
            .Where(static i => i.IsGenericType)
            .Any(i => contexts.Any(c => c.GetTypeInfo(i, options) is not null));
    }
}
