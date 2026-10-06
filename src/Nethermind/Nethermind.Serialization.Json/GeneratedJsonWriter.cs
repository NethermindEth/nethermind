// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Nethermind.Serialization.Json;

/// <summary>Marks a converter emitted by the JSON writer generator.</summary>
internal interface IGeneratedJsonWriter
{
    /// <summary>The exact type the writer writes.</summary>
    Type WrittenType { get; }

    /// <summary>Whether writes with <paramref name="options"/> use the generated code rather than the metadata path.</summary>
    bool IsActive(JsonSerializerOptions options);

    /// <summary>Gets the state writes with <paramref name="options"/> use, or <see langword="null"/> when they use the metadata path.</summary>
    /// <remarks>The same instance for every call with the options the writer is bound to; a new one for any other options.</remarks>
    object? GetState(JsonSerializerOptions options);

    /// <summary>Writes <paramref name="value"/>, an instance of exactly <see cref="WrittenType"/>.</summary>
    void WriteValue(Utf8JsonWriter writer, object value, JsonSerializerOptions options);
}

/// <summary>Marks the factory that registers a generated writer with serializer options.</summary>
internal interface IGeneratedJsonWriterFactory
{
    /// <summary>The exact type the created writers write.</summary>
    Type WrittenType { get; }
}

/// <summary>Creates a generated writer for <typeparamref name="T"/> bound to the options STJ creates it for.</summary>
/// <remarks>
/// STJ calls <see cref="CreateConverter"/> once per options caching context and keeps the result in that context's metadata,
/// so each writer lives exactly as long as the options it serves.
/// </remarks>
internal sealed class GeneratedJsonWriterFactory<T, TWriter>(Func<JsonSerializerOptions, TWriter> create) : JsonConverterFactory, IGeneratedJsonWriterFactory
    where TWriter : JsonConverter<T>, IGeneratedJsonWriter
{
    public Type WrittenType => typeof(T);

    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => create(options);
}

/// <summary>How a generated writer treats one entry of the type's metadata contract.</summary>
internal enum GeneratedJsonPropertyKind : byte
{
    /// <summary>The writer writes the property.</summary>
    Written,

    /// <summary>The property has no usable getter, so neither path writes it.</summary>
    NotWritten,

    /// <summary>The property carries <c>[JsonIgnore]</c> with <see cref="JsonIgnoreCondition.Always"/>.</summary>
    Ignored,
}

/// <summary>One entry of the metadata contract a generated writer was emitted for, in contract order.</summary>
internal readonly struct GeneratedJsonProperty(string memberName, string? explicitName, Type propertyType, Type? converterType, GeneratedJsonPropertyKind kind)
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
/// the metadata path through a copy of the options without the generated writers.
/// A writer used only through a dispatching converter is never registered with options, so STJ never reads through it.
/// </para>
/// <para>
/// A writer keeps the state and the metadata copy for one read-only options instance: the one it was created for, or else
/// the first one it serves. STJ creates one writer per options caching context and passes that context's options to every
/// write it drives, so only a direct call with other options builds the state again, and that state is not kept.
/// </para>
/// </remarks>
internal abstract class GeneratedJsonWriter<T, TState> : JsonConverter<T>, IGeneratedJsonWriter
    where T : class
    where TState : class
{
    private readonly JsonSerializerOptions? _owner;
    private Binding? _binding;

    protected GeneratedJsonWriter() { }

    /// <param name="owner">The options STJ creates the writer for; it then keeps state for no other instance.</param>
    protected GeneratedJsonWriter(JsonSerializerOptions owner) => _owner = owner;

    public sealed override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    // Null also goes to the metadata path, so a converter registered after this one still sees it.
    public sealed override bool HandleNull => true;

    public sealed override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TypeInfoJsonSerializer.Deserialize<T>(ref reader, GetBinding(options).MetadataOptions);

    public sealed override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        Binding binding = GetBinding(options);
        if (value is not null && binding.State is { } state)
        {
            Write(writer, value, state, options);
        }
        else
        {
            TypeInfoJsonSerializer.Serialize(writer, value, binding.MetadataOptions);
        }
    }

    public Type WrittenType => typeof(T);

    public bool IsActive(JsonSerializerOptions options) => GetBinding(options).State is not null;

    public object? GetState(JsonSerializerOptions options) => GetBinding(options).State;

    public void WriteValue(Utf8JsonWriter writer, object value, JsonSerializerOptions options) => Write(writer, (T)value, options);

    /// <summary>Builds the state for <paramref name="options"/>, or returns <see langword="null"/> to defer to the metadata path.</summary>
    /// <param name="metadataOptions">The copy of <paramref name="options"/> without the generated writers.</param>
    protected abstract TState? CreateState(JsonSerializerOptions options, JsonSerializerOptions metadataOptions);

    /// <summary>Writes a non-null <paramref name="value"/> with the state built for <paramref name="options"/>.</summary>
    protected abstract void Write(Utf8JsonWriter writer, T value, TState state, JsonSerializerOptions options);

    private Binding GetBinding(JsonSerializerOptions options)
    {
        Binding? binding = Volatile.Read(ref _binding);
        return binding is not null && ReferenceEquals(binding.Options, options) ? binding : Bind(options, binding);
    }

    private Binding Bind(JsonSerializerOptions options, Binding? current)
    {
        // Read before building: options that were mutable when copied could have changed since, so their state is never kept.
        bool keep = current is null && options.IsReadOnly && (_owner is null || ReferenceEquals(_owner, options));
        JsonSerializerOptions metadataOptions = GeneratedJsonWriters.CreateMetadataOptions(options);
        Binding created = new(options, CreateState(options, metadataOptions), metadataOptions);

        if (keep)
        {
            // Threads that race here build equal state; one publication wins and the others use their own once.
            Interlocked.CompareExchange(ref _binding, created, null);
        }

        return created;
    }

    private sealed class Binding(JsonSerializerOptions options, TState? state, JsonSerializerOptions metadataOptions)
    {
        public readonly JsonSerializerOptions Options = options;
        public readonly TState? State = state;
        public readonly JsonSerializerOptions MetadataOptions = metadataOptions;
    }
}

/// <summary>The dispatch writers one hand-written converter instance uses, created on first use of each type.</summary>
/// <remarks>
/// STJ creates a converter named by a type-level <c>[JsonConverter]</c> once per options caching context, so a converter that
/// owns this set gives each options instance writers, and so writer state, of its own.
/// </remarks>
internal sealed class GeneratedJsonDispatch
{
    private readonly Lock _addLock = new();
    private Dictionary<Type, IGeneratedJsonWriter?> _writers = [];

    /// <summary>Gets the writer for exactly <paramref name="type"/>, or <see langword="false"/> when none is registered.</summary>
    /// <remarks>Copy-on-write, so lookups take no lock and readers never see a partly built table.</remarks>
    public bool TryGetWriter(Type type, [NotNullWhen(true)] out IGeneratedJsonWriter? writer)
    {
        if (!Volatile.Read(ref _writers).TryGetValue(type, out writer)) writer = Add(type);
        return writer is not null;
    }

    private IGeneratedJsonWriter? Add(Type type)
    {
        lock (_addLock)
        {
            if (_writers.TryGetValue(type, out IGeneratedJsonWriter? writer)) return writer;

            // A type that has no writer now never gets one: its assembly registers it before any instance can exist.
            GeneratedJsonWriters.TryCreateDispatchWriter(type, out writer);
            Volatile.Write(ref _writers, new Dictionary<Type, IGeneratedJsonWriter?>(_writers) { [type] = writer });
            return writer;
        }
    }
}

/// <summary>Runtime support shared by the converters the JSON writer generator emits.</summary>
internal static class GeneratedJsonWriters
{
    private const int DefaultMaxDepth = 64;

    private static readonly Lock DispatchLock = new();
    private static Dictionary<Type, Func<IGeneratedJsonWriter>> _dispatchWriters = [];

    /// <summary>Registers the writer for <typeparamref name="T"/> with every options instance <see cref="EthereumJsonSerializer"/> builds.</summary>
    /// <remarks>Registered through a factory, so each options instance gets a writer of its own.</remarks>
    public static void Register<T, TWriter>(Func<JsonSerializerOptions, TWriter> create)
        where TWriter : JsonConverter<T>, IGeneratedJsonWriter =>
        EthereumJsonSerializer.AddConverter(new GeneratedJsonWriterFactory<T, TWriter>(create));

    /// <summary>Makes the writer for <typeparamref name="T"/> available to the hand-written converters that dispatch to the type.</summary>
    /// <remarks>Copy-on-write, so lookups take no lock; registrations happen at module initialization.</remarks>
    public static void RegisterForDispatch<T>(Func<IGeneratedJsonWriter> create)
    {
        lock (DispatchLock)
        {
            Volatile.Write(ref _dispatchWriters, new Dictionary<Type, Func<IGeneratedJsonWriter>>(_dispatchWriters) { [typeof(T)] = create });
        }
    }

    /// <summary>Creates a writer for exactly <paramref name="type"/> when one is registered for dispatch.</summary>
    /// <remarks>A dispatching converter keeps the writers it creates, so they live as long as its options do.</remarks>
    public static bool TryCreateDispatchWriter(Type type, [NotNullWhen(true)] out IGeneratedJsonWriter? writer)
    {
        writer = Volatile.Read(ref _dispatchWriters).TryGetValue(type, out Func<IGeneratedJsonWriter>? create) ? create() : null;
        return writer is not null;
    }

    /// <summary>Creates a copy of <paramref name="options"/> without generated writers, so types resolve through their metadata.</summary>
    public static JsonSerializerOptions CreateMetadataOptions(JsonSerializerOptions options)
    {
        JsonSerializerOptions copy = new(options);
        for (int i = copy.Converters.Count - 1; i >= 0; i--)
        {
            if (copy.Converters[i] is IGeneratedJsonWriter or IGeneratedJsonWriterFactory) copy.Converters.RemoveAt(i);
        }

        return copy;
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
    /// <param name="propertyConverters">The property-level converter instances the metadata path uses, by contract index.</param>
    /// <param name="metadataOptions">The copy of <paramref name="options"/> without the generated writers.</param>
    public static JsonEncodedText[]? GetPropertyNames<T>(JsonSerializerOptions options, JsonSerializerOptions metadataOptions, GeneratedJsonProperty[] contract,
        bool hasOnSerializing, bool hasOnSerialized, out JsonConverter?[] propertyConverters)
    {
        propertyConverters = new JsonConverter?[contract.Length];
        if (!IsContractUncustomized(typeof(T), metadataOptions)) return null;

        JsonTypeInfo info = TypeInfoJsonSerializer.GetTypeInfo<T>(metadataOptions);
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
                !IsExpectedConverter(property.CustomConverter, expected) ||
                property.IsExtensionData ||
                property.NumberHandling is not null ||
                (expected.Kind == GeneratedJsonPropertyKind.Written && property.Get is null) ||
                (expected.Kind == GeneratedJsonPropertyKind.NotWritten && property.Get is not null))
            {
                return null;
            }

            names[i] = JsonEncodedText.Encode(name, options.Encoder);
            propertyConverters[i] = property.CustomConverter;
        }

        return names;
    }

    // Source-generated metadata expands a factory named by the attribute into the converter it creates for the property type.
    private static bool IsExpectedConverter(JsonConverter? actual, in GeneratedJsonProperty expected) =>
        expected.ConverterType is null
            ? actual is null
            : actual is not null && (actual.GetType() == expected.ConverterType ||
                (typeof(JsonConverterFactory).IsAssignableFrom(expected.ConverterType) && actual is not JsonConverterFactory && actual.Type == expected.PropertyType));

    /// <summary>
    /// Whether the resolver that supplies <paramref name="type"/>'s metadata builds it unmodified, so the property checks above
    /// see every setting that affects writing.
    /// </summary>
    /// <remarks>
    /// A resolver modifier can change predicates and accessors the checks cannot compare, such as
    /// <see cref="JsonPropertyInfo.ShouldSerialize"/>; such contracts stay on the metadata path.
    /// </remarks>
    private static bool IsContractUncustomized(Type type, JsonSerializerOptions options)
    {
        foreach (IJsonTypeInfoResolver resolver in options.TypeInfoResolverChain)
        {
            if (resolver.GetTypeInfo(type, options) is null) continue;

            // The first resolver that answers is the one the options use.
            // Exact type: a derived resolver can override GetTypeInfo and customize the contract itself.
            return resolver is JsonSerializerContext ||
                (resolver.GetType() == typeof(DefaultJsonTypeInfoResolver) && ((DefaultJsonTypeInfoResolver)resolver).Modifiers.Count == 0);
        }

        return false;
    }

    /// <summary>
    /// Gets the converter the metadata path uses for a property of type <typeparamref name="TProperty"/>: the contract's
    /// property-level converter when there is one, otherwise the one <paramref name="options"/> resolve for the type.
    /// </summary>
    /// <returns><see langword="false"/> when that converter is not a <see cref="JsonConverter{T}"/> of exactly <typeparamref name="TProperty"/>.</returns>
    /// <remarks>Read from metadata, as the metadata path does, so no reflection is needed.</remarks>
    public static bool TryGetConverter<TProperty>(JsonConverter? propertyConverter, JsonSerializerOptions options, [NotNullWhen(true)] out JsonConverter? converter)
    {
        JsonConverter resolved = propertyConverter switch
        {
            null => TypeInfoJsonSerializer.GetTypeInfo<TProperty>(options).Converter,
            JsonConverterFactory factory => factory.CreateConverter(typeof(TProperty), options)!,
            _ => propertyConverter,
        };

        converter = resolved as JsonConverter<TProperty>;
        return converter is not null;
    }

    public static int GetMaxDepth(JsonSerializerOptions options) => options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth;

    [DoesNotReturn]
    public static void ThrowMaxDepthExceeded(int maxDepth) =>
        throw new JsonException($"A possible object cycle was detected. This can either be due to a cycle or if the object depth is larger than the maximum allowed depth of {maxDepth}. Consider using ReferenceHandler.IgnoreCycles on JsonSerializerOptions to support cycles.");
}
