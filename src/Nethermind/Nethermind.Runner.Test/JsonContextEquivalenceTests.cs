// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

/// <summary>
/// Source-generated JSON contexts replace reflection metadata, so each must describe the same JSON contract reflection does.
/// </summary>
public class JsonContextEquivalenceTests
{
    private static readonly Lazy<JsonSerializerContext[]> Contexts = new(LoadContexts);

    private static IEnumerable<TestCaseData> ContextCases() =>
        Contexts.Value.Select(static context => new TestCaseData(context).SetArgDisplayNames(context.GetType().FullName!));

    [TestCaseSource(nameof(ContextCases))]
    public void Context_metadata_matches_reflection(JsonSerializerContext context)
    {
        // Contexts in the EthereumJsonSerializer chain serve its options; the rest are used with their own.
        // Generated writers would hide the metadata they defer to, so compare it without them.
        JsonSerializerOptions options = EthereumJsonSerializer.JsonOptions.TypeInfoResolverChain.Contains(context)
            ? GeneratedJsonWriters.CreateMetadataOptions(EthereumJsonSerializer.JsonOptions)
            : context.Options;

        List<string> differences = [];
        DefaultJsonTypeInfoResolver reflection = new();
        HashSet<Type> visited = [];
        Queue<Type> pending = new(GetRoots(context));
        IJsonTypeInfoResolver resolver = context;

        while (pending.TryDequeue(out Type type))
        {
            if (!visited.Add(type) || resolver.GetTypeInfo(type, options) is not { } generated)
            {
                continue;
            }

            JsonTypeInfo expected = reflection.GetTypeInfo(type, options);
            Compare(type, expected, generated, differences);

            foreach (JsonPropertyInfo property in expected.Properties)
            {
                pending.Enqueue(property.PropertyType);
            }

            if (expected.ElementType is { } elementType) pending.Enqueue(elementType);
            if (expected.KeyType is { } keyType) pending.Enqueue(keyType);
        }

        Assert.That(differences, Is.Empty);
    }

    private static IEnumerable<Type> GetRoots(JsonSerializerContext context) =>
        context.GetType().GetCustomAttributesData()
            .Where(static a => a.AttributeType == typeof(JsonSerializableAttribute))
            .Select(static a => (Type)a.ConstructorArguments[0].Value!);

    private static void Compare(Type type, JsonTypeInfo expected, JsonTypeInfo actual, List<string> differences)
    {
        void Check<T>(string what, T reflectionValue, T generatedValue)
        {
            if (!EqualityComparer<T>.Default.Equals(reflectionValue, generatedValue))
            {
                differences.Add($"{type}: {what} reflection={reflectionValue} generated={generatedValue}");
            }
        }

        Check("Kind", expected.Kind, actual.Kind);
        // Source generation wraps built-in converters; only custom converters must match exactly.
        Check("Converter", CustomConverter(expected.Converter), CustomConverter(actual.Converter));
        Check("NumberHandling", expected.NumberHandling, actual.NumberHandling);
        Check("UnmappedMemberHandling", expected.UnmappedMemberHandling, actual.UnmappedMemberHandling);
        Check("Polymorphism", Describe(expected.PolymorphismOptions), Describe(actual.PolymorphismOptions));

        if (expected.Kind != JsonTypeInfoKind.Object || actual.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        Check("Properties", Describe(expected.Properties), Describe(actual.Properties));
        CheckWritable(type, expected, actual, differences);
        CheckInitializers(type, expected, actual, differences);
    }

    private static Type CustomConverter(JsonConverter converter) =>
        converter.GetType().Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true ? null : converter.GetType();

    private static string Describe(JsonPolymorphismOptions options) =>
        options is null
            ? null
            : $"{options.TypeDiscriminatorPropertyName}:{string.Join(",", options.DerivedTypes.Select(static d => $"{d.DerivedType}={d.TypeDiscriminator}"))}";

    // A member with neither accessor never reaches JSON, so its type does not matter.
    private static string Describe(IList<JsonPropertyInfo> properties) =>
        string.Join("; ", properties.Select(static p =>
            $"{p.Name}:{(p.Get is null && p.Set is null ? "-" : p.PropertyType)}:order={p.Order}:required={p.IsRequired}:ext={p.IsExtensionData}:get={p.Get is not null}:" +
            $"conv={p.CustomConverter?.GetType()}:num={p.NumberHandling}"));

    /// <remarks>
    /// Source generation binds init-only members as constructor parameters where reflection uses setters, and it can populate
    /// readonly fields reflection cannot, so it only has to populate at least what reflection does.
    /// </remarks>
    private static void CheckWritable(Type type, JsonTypeInfo expected, JsonTypeInfo actual, List<string> differences)
    {
        foreach (JsonPropertyInfo property in expected.Properties)
        {
            if ((property.Set is not null || property.AssociatedParameter is not null) &&
                actual.Properties.FirstOrDefault(p => p.Name == property.Name) is { Set: null, AssociatedParameter: null })
            {
                differences.Add($"{type}: {property.Name} is not populated on deserialization");
            }
        }
    }

    /// <summary>Init-only initializers whose loss was reviewed, with the reason it does not change behavior.</summary>
    private static readonly Dictionary<Type, string> ReviewedInitializerLosses = new()
    {
        // A consensus client's identity: absent fields stay empty rather than taking Nethermind's own, on both the JSON-RPC and SSZ endpoints.
        [typeof(Nethermind.Merge.Plugin.Data.ClientVersionV1)] = "peer identity",
        // RPC results, never deserialized.
        [typeof(Nethermind.JsonRpc.Data.AccountInfoForRpc)] = "response only",
        [typeof(Nethermind.Xdc.RPC.XdcAccountInfo)] = "response only",
        [typeof(Nethermind.Xdc.RPC.XdcTransactionAndReceiptProof)] = "response only",
    };

    /// <remarks>
    /// Source generation passes default values for absent constructor-bound members, so an initializer on an init-only member
    /// is lost when the JSON omits it, while reflection keeps it.
    /// </remarks>
    private static void CheckInitializers(Type type, JsonTypeInfo expected, JsonTypeInfo actual, List<string> differences)
    {
        if (expected.CreateObject is null || ReviewedInitializerLosses.ContainsKey(type))
        {
            return;
        }

        object instance = expected.CreateObject();
        foreach (JsonPropertyInfo property in actual.Properties)
        {
            if (property.AssociatedParameter is null || property.IsRequired || property.Get is null ||
                expected.Properties.FirstOrDefault(p => p.Name == property.Name) is not { AssociatedParameter: null })
            {
                continue;
            }

            object value = property.Get(instance);
            object defaultValue = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
            if (!Equals(value, defaultValue))
            {
                differences.Add($"{type}: init-only {property.Name} initializer {value} is lost when the JSON omits it");
            }
        }
    }

    /// <remarks>Running every module initializer registers the contexts the assemblies own, as the node does at startup.</remarks>
    private static JsonSerializerContext[] LoadContexts()
    {
        string directory = Path.GetDirectoryName(typeof(JsonContextEquivalenceTests).Assembly.Location)!;
        List<JsonSerializerContext> contexts = [];
        foreach (string path in Directory.GetFiles(directory, "Nethermind.*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            if (assembly.GetName().Name!.EndsWith(".Test", StringComparison.Ordinal))
            {
                continue;
            }

            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type.IsSubclassOf(typeof(JsonSerializerContext)) && !type.IsAbstract &&
                    type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is JsonSerializerContext context)
                {
                    contexts.Add(context);
                }
            }
        }

        return [.. contexts];
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(static t => t is not null)!;
        }
    }
}
