// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal enum JsonRpcIdKind : byte
{
    Missing,
    Null,
    Long,
    Decimal,
    String
}

/// <summary>
/// Represents a JSON-RPC request or response ID without boxing primitive ID values.
/// </summary>
/// <remarks>
/// The missing and explicit-null states are distinct so parsing can preserve the
/// request envelope shape. Both states currently serialize as JSON null to keep
/// existing Nethermind wire behavior.
/// </remarks>
public readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
    private readonly string? _stringValue;
    private readonly long _longValue;
    private readonly decimal _decimalValue;
    private readonly JsonRpcIdKind _kind;

    private JsonRpcId(JsonRpcIdKind kind) => _kind = kind;

    /// <summary>
    /// Gets an ID value representing an absent JSON-RPC ID property.
    /// </summary>
    public static JsonRpcId Missing => default;

    /// <summary>
    /// Gets an ID value representing an explicit JSON null.
    /// </summary>
    public static JsonRpcId Null => new(JsonRpcIdKind.Null);

    /// <summary>
    /// Initializes an ID from a signed 64-bit integer.
    /// </summary>
    /// <param name="value">The integer ID value.</param>
    public JsonRpcId(long value)
    {
        _kind = JsonRpcIdKind.Long;
        _longValue = value;
    }

    /// <summary>
    /// Initializes an ID from an integer decimal value.
    /// </summary>
    /// <param name="value">The decimal ID value. Fractional decimals are not valid JSON-RPC IDs in this compatibility representation.</param>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="value"/> has a fractional component.</exception>
    public JsonRpcId(decimal value)
    {
        if (value.Scale != 0)
        {
            ThrowFractionalDecimal();
        }

        _kind = JsonRpcIdKind.Decimal;
        _decimalValue = value;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowFractionalDecimal() =>
            throw new NotSupportedException("JSON-RPC decimal IDs must be integer values.");
    }

    /// <summary>
    /// Initializes an ID from a string.
    /// </summary>
    /// <param name="value">The string ID value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public JsonRpcId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _kind = JsonRpcIdKind.String;
        _stringValue = value;
    }

    /// <summary>
    /// Gets whether this value represents an absent JSON-RPC ID property.
    /// </summary>
    public bool IsMissing => _kind == JsonRpcIdKind.Missing;

    /// <summary>
    /// Gets whether this value represents an explicit JSON null ID.
    /// </summary>
    public bool IsNull => _kind == JsonRpcIdKind.Null;

    /// <summary>
    /// Gets whether this value represents either a missing or explicit-null ID.
    /// </summary>
    public bool IsNullLike => _kind is JsonRpcIdKind.Missing or JsonRpcIdKind.Null;

    /// <summary>
    /// Converts the legacy boxed ID representation into a typed JSON-RPC ID.
    /// </summary>
    /// <param name="value">The legacy ID value.</param>
    /// <returns>The equivalent typed ID.</returns>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="value"/> is not a supported JSON-RPC ID type.</exception>
    public static JsonRpcId FromObject(object? value)
    {
        return value switch
        {
            null => Null,
            JsonRpcId jsonRpcId => jsonRpcId,
            int intValue => new JsonRpcId(intValue),
            long longValue => new JsonRpcId(longValue),
            decimal decimalValue => new JsonRpcId(decimalValue),
            BigInteger bigIntegerValue => new JsonRpcId((decimal)bigIntegerValue),
            string stringValue => new JsonRpcId(stringValue),
            _ => ThrowUnsupportedObject(value)
        };

        [DoesNotReturn, StackTraceHidden]
        static JsonRpcId ThrowUnsupportedObject(object value) =>
            throw new NotSupportedException($"Unsupported JSON-RPC ID type: {value.GetType().FullName}");
    }

    /// <summary>
    /// Converts this typed ID to the legacy boxed representation.
    /// </summary>
    /// <returns>The boxed ID value, or null for missing and explicit-null IDs.</returns>
    public object? ToObject()
    {
        return _kind switch
        {
            JsonRpcIdKind.Missing or JsonRpcIdKind.Null => null,
            JsonRpcIdKind.Long => _longValue,
            JsonRpcIdKind.Decimal => _decimalValue,
            JsonRpcIdKind.String => _stringValue,
            _ => ThrowInvalidKindObject()
        };

        [DoesNotReturn, StackTraceHidden]
        static object ThrowInvalidKindObject() =>
            throw new NotSupportedException("Unsupported JSON-RPC ID kind.");
    }

    /// <summary>
    /// Writes this ID as a JSON value.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is null.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (_kind)
        {
            case JsonRpcIdKind.Missing:
            case JsonRpcIdKind.Null:
                writer.WriteNullValue();
                break;
            case JsonRpcIdKind.Long:
                writer.WriteNumberValue(_longValue);
                break;
            case JsonRpcIdKind.Decimal:
                writer.WriteNumberValue(_decimalValue);
                break;
            case JsonRpcIdKind.String:
                writer.WriteStringValue(_stringValue);
                break;
            default:
                ThrowInvalidKind();
                break;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidKind() =>
            throw new NotSupportedException("Unsupported JSON-RPC ID kind.");
    }

    /// <summary>
    /// Tries to get this ID as a signed 64-bit integer.
    /// </summary>
    /// <param name="value">The integer ID value when this method returns true.</param>
    /// <returns>True when this ID stores a signed 64-bit integer; otherwise false.</returns>
    public bool TryGetInt64(out long value)
    {
        value = _longValue;
        return _kind == JsonRpcIdKind.Long;
    }

    /// <summary>
    /// Tries to get this ID as a decimal integer.
    /// </summary>
    /// <param name="value">The decimal ID value when this method returns true.</param>
    /// <returns>True when this ID stores a decimal integer; otherwise false.</returns>
    public bool TryGetDecimal(out decimal value)
    {
        value = _decimalValue;
        return _kind == JsonRpcIdKind.Decimal;
    }

    /// <summary>
    /// Tries to get this ID as a string.
    /// </summary>
    /// <param name="value">The string ID value when this method returns true.</param>
    /// <returns>True when this ID stores a string; otherwise false.</returns>
    public bool TryGetString(out string? value)
    {
        value = _stringValue;
        return _kind == JsonRpcIdKind.String;
    }

    /// <inheritdoc/>
    public bool Equals(JsonRpcId other) =>
        _kind == other._kind &&
        _kind switch
        {
            JsonRpcIdKind.Long => _longValue == other._longValue,
            JsonRpcIdKind.Decimal => _decimalValue == other._decimalValue,
            JsonRpcIdKind.String => string.Equals(_stringValue, other._stringValue, StringComparison.Ordinal),
            _ => true
        };

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj switch
        {
            null => IsNullLike,
            JsonRpcId other => Equals(other),
            int intValue => _kind == JsonRpcIdKind.Long && _longValue == intValue,
            long longValue => _kind == JsonRpcIdKind.Long && _longValue == longValue,
            decimal decimalValue => _kind == JsonRpcIdKind.Decimal && _decimalValue == decimalValue,
            string stringValue => _kind == JsonRpcIdKind.String && string.Equals(_stringValue, stringValue, StringComparison.Ordinal),
            _ => false
        };

    /// <inheritdoc/>
    public override int GetHashCode() =>
        _kind switch
        {
            JsonRpcIdKind.Long => HashCode.Combine(_kind, _longValue),
            JsonRpcIdKind.Decimal => HashCode.Combine(_kind, _decimalValue),
            JsonRpcIdKind.String => HashCode.Combine(_kind, _stringValue),
            _ => _kind.GetHashCode()
        };

    /// <summary>
    /// Returns a diagnostic representation of the ID.
    /// </summary>
    /// <returns>The ID as text, or a marker for missing/null states.</returns>
    public override string ToString() =>
        _kind switch
        {
            JsonRpcIdKind.Missing => "<missing>",
            JsonRpcIdKind.Null => "null",
            JsonRpcIdKind.Long => _longValue.ToString(CultureInfo.InvariantCulture),
            JsonRpcIdKind.Decimal => _decimalValue.ToString(CultureInfo.InvariantCulture),
            JsonRpcIdKind.String => _stringValue ?? string.Empty,
            _ => string.Empty
        };

    /// <summary>
    /// Determines whether two IDs are equal.
    /// </summary>
    /// <param name="left">The left ID.</param>
    /// <param name="right">The right ID.</param>
    /// <returns>True when the IDs are equal; otherwise false.</returns>
    public static bool operator ==(JsonRpcId left, JsonRpcId right) => left.Equals(right);

    /// <summary>
    /// Determines whether two IDs are not equal.
    /// </summary>
    /// <param name="left">The left ID.</param>
    /// <param name="right">The right ID.</param>
    /// <returns>True when the IDs are not equal; otherwise false.</returns>
    public static bool operator !=(JsonRpcId left, JsonRpcId right) => !left.Equals(right);

    /// <summary>
    /// Converts an integer to a JSON-RPC ID.
    /// </summary>
    /// <param name="value">The integer value.</param>
    public static implicit operator JsonRpcId(int value) => new(value);

    /// <summary>
    /// Converts a signed 64-bit integer to a JSON-RPC ID.
    /// </summary>
    /// <param name="value">The integer value.</param>
    public static implicit operator JsonRpcId(long value) => new(value);

    /// <summary>
    /// Converts an integer decimal to a JSON-RPC ID.
    /// </summary>
    /// <param name="value">The decimal value.</param>
    public static implicit operator JsonRpcId(decimal value) => new(value);

    /// <summary>
    /// Converts a string to a JSON-RPC ID.
    /// </summary>
    /// <param name="value">The string value.</param>
    public static implicit operator JsonRpcId(string value) => new(value);

}
