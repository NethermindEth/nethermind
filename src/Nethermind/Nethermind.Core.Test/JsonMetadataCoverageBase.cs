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
using System.Threading;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// Fails the test that serializes a type no source-generated context covers.
/// </summary>
/// <remarks>
/// Native AOT has no reflection-based metadata, so such a type fails to serialize there. A collection whose runtime type is
/// uncovered still serializes through a covered collection interface it implements, so a non-public one with such an interface,
/// such as a compiler-generated iterator, is not reported; a public collection must be rooted, as its interface may change the JSON shape.
/// NUnit runs only the set-up fixtures declared in the assembly under test, so each test assembly derives its own.
/// The serializer caches metadata per options, so a type first resolved as part of a test-only shape is not checked again when
/// production code later serializes it in the same process; keep test-only shapes to types production code does not write.
/// </remarks>
/// <param name="testOnly">Uncovered types only test code serializes, each with the reason no production path needs them.</param>
public abstract class JsonMetadataCoverageBase(params IReadOnlyDictionary<Type, string>[] testOnly)
{
    private static readonly ConcurrentDictionary<Type, string> Uncovered = new();

    private sealed class Recorder(IReadOnlyDictionary<Type, string>[] testOnly) : IJsonTypeInfoResolver
    {
        // Guarded so a concurrent request for a shape waits until its members are known.
        private readonly HashSet<Type> _testShapeMembers = [];
        private readonly Lock _lock = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (IsTestOwned(type) || testOnly.Any(t => t.ContainsKey(type)))
            {
                // The members of a shape only tests serialize are asked for next, so they are exempt for the rest of the run.
                AddMembers(type, options);
            }
            else if (!IsClearScript(type) && !IsTestShapeMember(type))
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

        private bool IsTestShapeMember(Type type)
        {
            lock (_lock) return _testShapeMembers.Contains(type);
        }

        private void AddMembers(Type root, JsonSerializerOptions options)
        {
            lock (_lock) AddMembersLocked(root, options);
        }

        private void AddMembersLocked(Type root, JsonSerializerOptions options)
        {
            if (_testShapeMembers.Contains(root))
            {
                return;
            }

            // Collected apart and added at the end, so a walk that throws leaves no shape half exempt.
            DefaultJsonTypeInfoResolver reflection = new();
            HashSet<Type> members = [];
            Queue<Type> pending = new([root]);
            while (pending.TryDequeue(out Type? type))
            {
                if (_testShapeMembers.Contains(type) || !members.Add(type) ||
                    options.TypeInfoResolverChain.OfType<JsonSerializerContext>().Any(c => ((IJsonTypeInfoResolver)c).GetTypeInfo(type, options) is not null))
                {
                    continue;
                }

                JsonTypeInfo info = reflection.GetTypeInfo(type, options);
                // A member that is never written asks for no metadata; one a converter writes may, through the options.
                foreach (JsonPropertyInfo property in info.Properties.Where(static p => !IsIgnored(p)))
                {
                    pending.Enqueue(property.PropertyType);
                }

                if (info.ElementType is { } elementType) pending.Enqueue(elementType);
                if (info.KeyType is { } keyType) pending.Enqueue(keyType);
            }

            _testShapeMembers.UnionWith(members);
        }

        private static bool IsIgnored(JsonPropertyInfo property) =>
            property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true)
                .Any(static a => ((JsonIgnoreAttribute)a).Condition == JsonIgnoreCondition.Always) == true;
    }

    [OneTimeSetUp]
    public void Setup() => EthereumJsonSerializer.AddTypeInfoResolver(new Recorder(testOnly), (JsonTypeInfoResolverPriority)int.MaxValue);

    [OneTimeTearDown]
    public void TearDown()
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("JSON_COVERAGE_OUT");
            if (path is not null)
            {
                File.WriteAllLines(path, Uncovered.Select(static kv => $"{kv.Key}{Environment.NewLine}{kv.Value}{Environment.NewLine}----"));
            }
        }
        finally
        {
            // A miss that production code catches, such as a fallback on a failed request, fails no test. The test platform ignores
            // failures in assembly teardown, so only ending the process fails that run; a run with failed tests already fails.
            if (!Uncovered.IsEmpty && TestContext.CurrentContext.Result.FailCount == 0)
            {
                Environment.FailFast($"Types without source-generated JSON metadata: {string.Join(", ", Uncovered.Select(static kv => kv.Key))}");
            }
        }
    }

    private static bool IsRuntimeTypeDispatch(string stackTrace) =>
        stackTrace.Contains("RpcPayloadTypeInfo", StringComparison.Ordinal) ||
        stackTrace.Contains("ResolvePolymorphicConverter", StringComparison.Ordinal);

    private static bool IsTestOwned(Type type) =>
        type.Assembly.GetName().Name!.EndsWith(".Test", StringComparison.Ordinal) ||
        type.IsGenericType && type.GetGenericArguments().Any(IsTestOwned) ||
        type.IsArray && IsTestOwned(type.GetElementType()!);

    // The JavaScript tracer runs on V8 through ClearScript, which binds host objects by reflection, so script values are not trim-safe either way.
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
