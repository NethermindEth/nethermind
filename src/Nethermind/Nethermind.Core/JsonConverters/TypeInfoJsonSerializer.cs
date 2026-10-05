// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Mirrors the <see cref="JsonSerializer"/> overloads that take <see cref="JsonSerializerOptions"/>, but resolves
/// metadata through <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/>.
/// </summary>
/// <remarks>
/// The options' <see cref="JsonSerializerOptions.TypeInfoResolver"/> chain decides where metadata comes from: the
/// registered source-generated contexts first, then reflection while it is enabled. Serialization output is the same as
/// the matching <see cref="JsonSerializer"/> overload. Native AOT apps disable reflection, so every type that goes
/// through here must be covered by a registered context; the trim analyzer cannot check that.
/// </remarks>
public static class TypeInfoJsonSerializer
{
    /// <inheritdoc cref="JsonSerializer.Serialize{TValue}(Utf8JsonWriter, TValue, JsonSerializerOptions)"/>
    public static void Serialize<TValue>(Utf8JsonWriter writer, TValue value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.Serialize(Utf8JsonWriter, object, Type, JsonSerializerOptions)"/>
    public static void Serialize(Utf8JsonWriter writer, object? value, Type inputType, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, GetTypeInfo(options, inputType));

    /// <inheritdoc cref="JsonSerializer.Serialize{TValue}(Stream, TValue, JsonSerializerOptions)"/>
    public static void Serialize<TValue>(Stream utf8Json, TValue value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(utf8Json, value, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions)"/>
    public static string Serialize<TValue>(TValue value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(value, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.SerializeToUtf8Bytes{TValue}(TValue, JsonSerializerOptions)"/>
    public static byte[] SerializeToUtf8Bytes<TValue>(TValue value, JsonSerializerOptions options) =>
        JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.SerializeToUtf8Bytes(object, Type, JsonSerializerOptions)"/>
    public static byte[] SerializeToUtf8Bytes(object? value, Type inputType, JsonSerializerOptions options) =>
        JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo(options, inputType));

    /// <inheritdoc cref="JsonSerializer.SerializeAsync{TValue}(Stream, TValue, JsonSerializerOptions, CancellationToken)"/>
    public static Task SerializeAsync<TValue>(Stream utf8Json, TValue value, JsonSerializerOptions options, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(utf8Json, value, GetTypeInfo<TValue>(options), cancellationToken);

    /// <inheritdoc cref="JsonSerializer.SerializeAsync{TValue}(PipeWriter, TValue, JsonSerializerOptions, CancellationToken)"/>
    public static Task SerializeAsync<TValue>(PipeWriter utf8Json, TValue value, JsonSerializerOptions options, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(utf8Json, value, GetTypeInfo<TValue>(options), cancellationToken);

    /// <inheritdoc cref="JsonSerializer.Deserialize{TValue}(ref Utf8JsonReader, JsonSerializerOptions)"/>
    public static TValue? Deserialize<TValue>(ref Utf8JsonReader reader, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(ref reader, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.Deserialize(ref Utf8JsonReader, Type, JsonSerializerOptions)"/>
    public static object? Deserialize(ref Utf8JsonReader reader, Type returnType, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(ref reader, GetTypeInfo(options, returnType));

    /// <inheritdoc cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions)"/>
    public static TValue? Deserialize<TValue>(string json, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(json, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)"/>
    public static object? Deserialize(string json, Type returnType, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(json, GetTypeInfo(options, returnType));

    /// <inheritdoc cref="JsonSerializer.Deserialize{TValue}(ReadOnlySpan{byte}, JsonSerializerOptions)"/>
    public static TValue? Deserialize<TValue>(ReadOnlySpan<byte> utf8Json, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(utf8Json, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.Deserialize(ReadOnlySpan{byte}, Type, JsonSerializerOptions)"/>
    public static object? Deserialize(ReadOnlySpan<byte> utf8Json, Type returnType, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(utf8Json, GetTypeInfo(options, returnType));

    /// <inheritdoc cref="JsonSerializer.Deserialize{TValue}(Stream, JsonSerializerOptions)"/>
    public static TValue? Deserialize<TValue>(Stream utf8Json, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(utf8Json, GetTypeInfo<TValue>(options));

    /// <inheritdoc cref="JsonSerializer.Deserialize{TValue}(JsonElement, JsonSerializerOptions)"/>
    public static TValue? Deserialize<TValue>(JsonElement element, JsonSerializerOptions options) =>
        element.Deserialize(GetTypeInfo<TValue>(options));

    /// <summary>Deserializes <paramref name="element"/>, or returns <see langword="null"/> when it has no value.</summary>
    public static TValue? Deserialize<TValue>(JsonElement? element, JsonSerializerOptions options) where TValue : class =>
        element is { } value ? Deserialize<TValue>(value, options) : null;

    /// <inheritdoc cref="JsonSerializer.Deserialize(JsonElement, Type, JsonSerializerOptions)"/>
    public static object? Deserialize(JsonElement element, Type returnType, JsonSerializerOptions options) =>
        element.Deserialize(GetTypeInfo(options, returnType));

    /// <inheritdoc cref="JsonSerializer.DeserializeAsync{TValue}(Stream, JsonSerializerOptions, CancellationToken)"/>
    public static ValueTask<TValue?> DeserializeAsync<TValue>(Stream utf8Json, JsonSerializerOptions options, CancellationToken cancellationToken = default) =>
        JsonSerializer.DeserializeAsync(utf8Json, GetTypeInfo<TValue>(options), cancellationToken);

    /// <summary>Gets the metadata the options resolve for <typeparamref name="T"/>.</summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions options) => TypeInfoCache<T>.Get(options);

    private static JsonTypeInfo GetTypeInfo(JsonSerializerOptions options, Type type)
    {
        // Unlocked options build new metadata on every call; lock them first, as JsonSerializer does.
        if (!options.IsReadOnly)
        {
            MakeReadOnly(options);
        }

        return options.GetTypeInfo(type);
    }

    /// <remarks>
    /// Converters call into the serializer once per nested value, so a lookup in the options' metadata cache on every call is
    /// measurable. Two remembered entries per type serve the request and response options together without evicting each
    /// other; misses replace them in turn, so an entry for options that are no longer used is gone after the next miss.
    /// Hits read the immutable entries without locking; misses replace them under a lock.
    /// </remarks>
    private static class TypeInfoCache<T>
    {
        private static readonly Lock _replaceLock = new();
        private static Entry? _first;
        private static Entry? _second;
        private static bool _replaceSecond;

        public static JsonTypeInfo<T> Get(JsonSerializerOptions options) =>
            TryGet(options) ?? Add(options);

        private static JsonTypeInfo<T>? TryGet(JsonSerializerOptions options)
        {
            Entry? entry = Volatile.Read(ref _first);
            if (entry is not null && ReferenceEquals(entry.Options, options))
            {
                return entry.TypeInfo;
            }

            entry = Volatile.Read(ref _second);
            return entry is not null && ReferenceEquals(entry.Options, options) ? entry.TypeInfo : null;
        }

        private static JsonTypeInfo<T> Add(JsonSerializerOptions options)
        {
            lock (_replaceLock)
            {
                if (TryGet(options) is { } cached)
                {
                    return cached;
                }

                JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)GetTypeInfo(options, typeof(T));
                Entry created = new(options, typeInfo);
                if (_replaceSecond) Volatile.Write(ref _second, created);
                else Volatile.Write(ref _first, created);
                _replaceSecond = !_replaceSecond;
                return typeInfo;
            }
        }

        private sealed class Entry(JsonSerializerOptions options, JsonTypeInfo<T> typeInfo)
        {
            public readonly JsonSerializerOptions Options = options;
            public readonly JsonTypeInfo<T> TypeInfo = typeInfo;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Adds the reflection resolver only when the reflection feature switch is on, as JsonSerializer does.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Adds the reflection resolver only when the reflection feature switch is on, as JsonSerializer does.")]
    private static void MakeReadOnly(JsonSerializerOptions options) =>
        options.MakeReadOnly(populateMissingResolver: JsonSerializer.IsReflectionEnabledByDefault);
}
