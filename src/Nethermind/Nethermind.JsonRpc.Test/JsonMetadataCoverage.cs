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
            if (!IsTestOwned(type) && !IsClearScript(type) && !TestOnly.ContainsKey(type))
            {
                string stackTrace = Environment.StackTrace;
                // The serializer asks about every ancestor of a polymorphic value while looking for a declared base; absence is the answer it expects.
                // A value written by its runtime type falls back to a covered collection interface, but a declared type must be covered itself.
                if (!stackTrace.Contains("FindNearestPolymorphicBaseType", StringComparison.Ordinal) &&
                    !(IsRuntimeTypeDispatch(stackTrace) && HasCoveredCollectionInterface(type, options)))
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

    /// <summary>Uncovered types only test code serializes, with the reason no production path needs them.</summary>
    private static readonly Dictionary<Type, string> TestOnly = new()
    {
        // Parameter types of a test RPC module; no production RPC method takes them.
        [typeof(JsonRpcContext)] = "test module parameter",
        [typeof(JsonRpcUrl)] = "test module parameter",
        [typeof(IJsonRpcDuplexClient)] = "test module parameter",
        [typeof(Nethermind.JsonRpc.Modules.RpcEndpoint)] = "test module parameter",
        [typeof(IReadOnlySet<string>)] = "member of the test module's JsonRpcUrl parameter",
        // Shapes only the tests build: their JSON-RPC client's responses, batch requests, request parameters and round trips.
        [typeof(Nethermind.JsonRpc.Client.JsonRpcResponse<string>)] = "test client response",
        [typeof(Nethermind.JsonRpc.Client.JsonRpcResponse<byte[]>)] = "test client response",
        [typeof(Nethermind.JsonRpc.Client.JsonRpcResponse<Nethermind.Facade.Eth.RpcTransaction.SetCodeTransactionForRpc>)] = "test client response",
        [typeof(List<Nethermind.JsonRpc.Modules.Admin.PeerInfo>)] = "test client response",
        [typeof(object[][])] = "test batch request",
        [typeof(Dictionary<Nethermind.Core.Address, string[]>)] = "test request parameter",
        [typeof(Dictionary<Nethermind.Core.Address, string>)] = "converter round trip",
        [typeof(Dictionary<Nethermind.Core.AddressAsKey, string>)] = "converter round trip",
        [typeof(Dictionary<Nethermind.Core.AddressAsKey, Dictionary<string, string>>)] = "converter round trip",
        [typeof(decimal)] = "converter round trip",
        [typeof(Dictionary<string, string>)] = "test comparison value",
    };

    private static bool IsRuntimeTypeDispatch(string stackTrace) =>
        stackTrace.Contains(nameof(RpcPayloadTypeInfo), StringComparison.Ordinal) ||
        stackTrace.Contains("ResolvePolymorphicConverter", StringComparison.Ordinal);

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
