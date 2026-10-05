// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Nethermind.Serialization.Json;

/// <summary>Marks a converter emitted by the JSON writer generator.</summary>
public interface IGeneratedJsonWriter
{
    /// <summary>Whether writes with <paramref name="options"/> use the generated code rather than the metadata path.</summary>
    bool IsActive(JsonSerializerOptions options);
}

/// <summary>How a generated writer treats one entry of the type's metadata contract.</summary>
public enum GeneratedJsonPropertyKind : byte
{
    /// <summary>The writer writes the property.</summary>
    Written,

    /// <summary>The property has no usable getter, so neither path writes it.</summary>
    NotWritten,

    /// <summary>The property carries <c>[JsonIgnore]</c> with <see cref="JsonIgnoreCondition.Always"/>.</summary>
    Ignored,
}

/// <summary>One entry of the metadata contract a generated writer was emitted for, in contract order.</summary>
public readonly struct GeneratedJsonProperty(string memberName, string? explicitName, Type propertyType, Type? converterType, GeneratedJsonPropertyKind kind)
{
    public string MemberName { get; } = memberName;

    /// <summary>The <c>[JsonPropertyName]</c> value, which bypasses the naming policy.</summary>
    public string? ExplicitName { get; } = explicitName;

    public Type PropertyType { get; } = propertyType;

    /// <summary>The property-level <c>[JsonConverter]</c> type, if any.</summary>
    public Type? ConverterType { get; } = converterType;

    public GeneratedJsonPropertyKind Kind { get; } = kind;
}

/// <summary>Base class of the converters the JSON writer generator emits for <typeparamref name="T"/>.</summary>
/// <remarks>
/// <para>
/// Writes go through the generated code only for options it can honour and whose metadata contract for
/// <typeparamref name="T"/> matches the one it was generated for; otherwise, and for every read, the converter defers to
/// the metadata path through <see cref="GeneratedJsonWriters.GetMetadataOptions"/>, options that lack the generated writers.
/// </para>
/// <para>The per-options state is cached in two entries, so request and response options do not evict each other.</para>
/// </remarks>
public abstract class GeneratedJsonWriter<T, TState> : JsonConverter<T>, IGeneratedJsonWriter
    where T : class
    where TState : class
{
    private readonly Lock _replaceLock = new();
    private Entry? _first;
    private Entry? _second;
    private bool _replaceSecond;

    public sealed override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    public sealed override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TypeInfoJsonSerializer.Deserialize<T>(ref reader, GeneratedJsonWriters.GetMetadataOptions(options));

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (GetState(options) is { } state)
        {
            Write(writer, value, state, options);
        }
        else
        {
            TypeInfoJsonSerializer.Serialize(writer, value, GeneratedJsonWriters.GetMetadataOptions(options));
        }
    }

    /// <summary>Whether writes with <paramref name="options"/> use the generated code rather than the metadata path.</summary>
    public bool IsActive(JsonSerializerOptions options) => GetState(options) is not null;

    /// <summary>Builds the state for <paramref name="options"/>, or returns <see langword="null"/> to defer to the metadata path.</summary>
    protected abstract TState? CreateState(JsonSerializerOptions options);

    /// <summary>Writes a non-null <paramref name="value"/> with the state built for <paramref name="options"/>.</summary>
    protected abstract void Write(Utf8JsonWriter writer, T value, TState state, JsonSerializerOptions options);

    private TState? GetState(JsonSerializerOptions options)
    {
        Entry? entry = Volatile.Read(ref _first);
        if (entry is not null && ReferenceEquals(entry.Options, options)) return entry.State;

        entry = Volatile.Read(ref _second);
        if (entry is not null && ReferenceEquals(entry.Options, options)) return entry.State;

        return AddState(options);
    }

    private TState? AddState(JsonSerializerOptions options)
    {
        lock (_replaceLock)
        {
            Entry? entry = _first;
            if (entry is not null && ReferenceEquals(entry.Options, options)) return entry.State;

            entry = _second;
            if (entry is not null && ReferenceEquals(entry.Options, options)) return entry.State;

            Entry created = new(options, CreateState(options));
            if (_replaceSecond) Volatile.Write(ref _second, created);
            else Volatile.Write(ref _first, created);
            _replaceSecond = !_replaceSecond;
            return created.State;
        }
    }

    private sealed class Entry(JsonSerializerOptions options, TState? state)
    {
        public readonly JsonSerializerOptions Options = options;
        public readonly TState? State = state;
    }
}

/// <summary>Runtime support shared by the converters the JSON writer generator emits.</summary>
public static class GeneratedJsonWriters
{
    private const int DefaultMaxDepth = 64;

    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> MetadataOptions = [];
    private static MetadataOptionsEntry? _lastMetadataOptions;

    /// <summary>Registers generated writers with every options instance <see cref="EthereumJsonSerializer"/> builds.</summary>
    public static void Register(params JsonConverter[] writers)
    {
        foreach (JsonConverter writer in writers)
        {
            EthereumJsonSerializer.AddConverter(writer);
        }
    }

    /// <summary>Gets a copy of <paramref name="options"/> without generated writers, so types resolve through their metadata.</summary>
    public static JsonSerializerOptions GetMetadataOptions(JsonSerializerOptions options)
    {
        MetadataOptionsEntry? last = Volatile.Read(ref _lastMetadataOptions);
        if (last is not null && ReferenceEquals(last.Options, options)) return last.MetadataOptions;

        JsonSerializerOptions metadataOptions = MetadataOptions.GetValue(options, static o => CreateMetadataOptions(o));
        Volatile.Write(ref _lastMetadataOptions, new MetadataOptionsEntry(options, metadataOptions));
        return metadataOptions;
    }

    /// <summary>Whether generated writers can honour every write-side setting of <paramref name="options"/>.</summary>
    /// <remarks>Field settings do not matter: the generator rejects types that have fields the metadata path could write.</remarks>
    public static bool SupportsOptions(JsonSerializerOptions options) =>
        options.ReferenceHandler is null &&
        (options.NumberHandling & (JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowNamedFloatingPointLiterals)) == 0 &&
        !options.IgnoreReadOnlyProperties &&
        !options.RespectNullableAnnotations &&
#pragma warning disable SYSLIB0020 // Obsolete, but still honoured by the metadata path.
        !options.IgnoreNullValues &&
#pragma warning restore SYSLIB0020
        options.DefaultIgnoreCondition is JsonIgnoreCondition.Never or JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault;

    /// <summary>
    /// Checks the metadata contract of <typeparamref name="T"/> under <paramref name="options"/> against the one a writer was
    /// generated for and returns the encoded property names, or <see langword="null"/> when they differ.
    /// </summary>
    public static JsonEncodedText[]? GetPropertyNames<T>(JsonSerializerOptions options, GeneratedJsonProperty[] contract, bool hasOnSerializing, bool hasOnSerialized)
    {
        JsonTypeInfo info = TypeInfoJsonSerializer.GetTypeInfo<T>(GetMetadataOptions(options));
        if (info.Kind != JsonTypeInfoKind.Object ||
            info.PolymorphismOptions is not null ||
            info.NumberHandling is not null ||
            info.OnSerializing is null == hasOnSerializing ||
            info.OnSerialized is null == hasOnSerialized ||
            info.Properties.Count != contract.Length)
        {
            return null;
        }

        JsonEncodedText[] names = new JsonEncodedText[contract.Length];
        for (int i = 0; i < contract.Length; i++)
        {
            JsonPropertyInfo property = info.Properties[i];
            GeneratedJsonProperty expected = contract[i];
            string name = expected.ExplicitName ?? options.PropertyNamingPolicy?.ConvertName(expected.MemberName) ?? expected.MemberName;
            if (property.Name != name) return null;

            // Source-generated metadata may type an ignored property as object, and neither path writes it.
            if (expected.Kind == GeneratedJsonPropertyKind.Ignored)
            {
                names[i] = JsonEncodedText.Encode(name, options.Encoder);
                continue;
            }

            if (property.PropertyType != expected.PropertyType ||
                property.CustomConverter?.GetType() != expected.ConverterType ||
                property.IsExtensionData ||
                property.NumberHandling is not null ||
                (expected.Kind == GeneratedJsonPropertyKind.Written && property.Get is null) ||
                (expected.Kind == GeneratedJsonPropertyKind.NotWritten && property.Get is not null))
            {
                return null;
            }

            names[i] = JsonEncodedText.Encode(name, options.Encoder);
        }

        return names;
    }

    /// <summary>Gets the converter <paramref name="options"/> resolve for a property of type <typeparamref name="TProperty"/>.</summary>
    /// <remarks>Read from the type's metadata, as the metadata path does, so no reflection is needed.</remarks>
    public static JsonConverter<TProperty> GetConverter<TProperty>(JsonSerializerOptions options) =>
        (JsonConverter<TProperty>)TypeInfoJsonSerializer.GetTypeInfo<TProperty>(options).Converter;

    /// <summary>Gets the converter the metadata path builds from a property-level <c>[JsonConverter]</c> attribute.</summary>
    public static JsonConverter<TProperty> GetConverter<TProperty>(JsonConverter attributeConverter, JsonSerializerOptions options) =>
        (JsonConverter<TProperty>)(attributeConverter is JsonConverterFactory factory
            ? factory.CreateConverter(typeof(TProperty), options)!
            : attributeConverter);

    public static int GetMaxDepth(JsonSerializerOptions options) => options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth;

    [DoesNotReturn]
    public static void ThrowMaxDepthExceeded(int maxDepth) =>
        throw new JsonException($"A possible object cycle was detected. This can either be due to a cycle or if the object depth is larger than the maximum allowed depth of {maxDepth}. Consider using ReferenceHandler.IgnoreCycles on JsonSerializerOptions to support cycles.");

    private static JsonSerializerOptions CreateMetadataOptions(JsonSerializerOptions options)
    {
        JsonSerializerOptions copy = new(options);
        for (int i = copy.Converters.Count - 1; i >= 0; i--)
        {
            if (copy.Converters[i] is IGeneratedJsonWriter) copy.Converters.RemoveAt(i);
        }

        return copy;
    }

    private sealed class MetadataOptionsEntry(JsonSerializerOptions options, JsonSerializerOptions metadataOptions)
    {
        public readonly JsonSerializerOptions Options = options;
        public readonly JsonSerializerOptions MetadataOptions = metadataOptions;
    }
}
