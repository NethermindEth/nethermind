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

namespace Nethermind.Core.Test;

/// <summary>
/// Fails the test that serializes a type no source-generated context covers.
/// </summary>
/// <remarks>
/// Native AOT has no reflection-based metadata, so such a type fails to serialize there. A collection whose runtime type is
/// uncovered still serializes through a covered collection interface it implements, so a non-public one with such an interface,
/// such as a compiler-generated iterator, is not reported; a public collection is rooted, as its interface may change the JSON shape.
/// NUnit runs only the set-up fixtures declared in the assembly under test, so each test assembly derives its own.
/// </remarks>
/// <param name="testOnly">Uncovered types only test code serializes, each with the reason no production path needs them.</param>
public abstract class JsonMetadataCoverageBase(params IReadOnlyDictionary<Type, string>[] testOnly)
{
    private static readonly ConcurrentDictionary<Type, string> Uncovered = new();

    private sealed class Recorder(IReadOnlyDictionary<Type, string>[] testOnly) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (!IsTestOwned(type) && !IsClearScript(type) && !testOnly.Any(t => t.ContainsKey(type)))
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
    public void Setup() => EthereumJsonSerializer.AddTypeInfoResolver(new Recorder(testOnly), (JsonTypeInfoResolverPriority)int.MaxValue);

    [OneTimeTearDown]
    public void TearDown()
    {
        string? path = Environment.GetEnvironmentVariable("JSON_COVERAGE_OUT");
        if (path is not null)
        {
            File.WriteAllLines(path, Uncovered.Select(static kv => $"{kv.Key}{Environment.NewLine}{kv.Value}{Environment.NewLine}----"));
        }
    }

    private static bool IsRuntimeTypeDispatch(string stackTrace) =>
        stackTrace.Contains("RpcPayloadTypeInfo", StringComparison.Ordinal) ||
        stackTrace.Contains("ResolvePolymorphicConverter", StringComparison.Ordinal);

    private static bool IsTestOwned(Type type) =>
        type.Assembly.GetName().Name!.EndsWith(".Test", StringComparison.Ordinal) ||
        type.IsGenericType && type.GetGenericArguments().Any(IsTestOwned) ||
        type.IsArray && IsTestOwned(type.GetElementType()!);

    private static bool IsClearScript(Type type) => type.Assembly.GetName().Name!.StartsWith("ClearScript", StringComparison.Ordinal);

    private static bool HasCoveredCollectionInterface(Type type, JsonSerializerOptions options)
    {
        if (type.IsVisible || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        IEnumerable<IJsonTypeInfoResolver> contexts = options.TypeInfoResolverChain.OfType<JsonSerializerContext>();
        return type.GetInterfaces()
            .Where(static i => i.IsGenericType)
            .Any(i => contexts.Any(c => c.GetTypeInfo(i, options) is not null));
    }
}
