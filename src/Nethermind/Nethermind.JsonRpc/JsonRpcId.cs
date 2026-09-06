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

/// <summary>Represents a JSON-RPC request or response ID without boxing primitive ID values.</summary>
/// <remarks>Missing and explicit-null IDs are distinct, but both serialize as JSON null for compatibility.</remarks>
public readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
    private static readonly JsonRpcId _null = new(JsonRpcIdKind.Null);

    private readonly byte[]? _rawValue;
    private readonly string? _stringValue;
    private readonly long _longValue;
    private readonly decimal _decimalValue;
    private readonly JsonRpcIdKind _kind;

    private JsonRpcId(JsonRpcIdKind kind) => _kind = kind;

    /// <summary>Gets an ID value representing an absent JSON-RPC ID property.</summary>
    public static JsonRpcId Missing => default;

    /// <summary>Gets an ID value representing an explicit JSON null.</summary>
    public static ref readonly JsonRpcId Null => ref _null;

    /// <summary>Initializes an ID from a signed 64-bit integer.</summary>
    /// <param name="value">The integer ID value.</param>
    public JsonRpcId(long value)
    {
        _kind = JsonRpcIdKind.Long;
        _longValue = value;
    }

    /// <summary>Initializes an ID from an integer decimal value.</summary>
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

    /// <summary>Initializes an ID from a string.</summary>
    /// <param name="value">The string ID value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public JsonRpcId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _kind = JsonRpcIdKind.String;
        _rawValue = JsonSerializer.SerializeToUtf8Bytes(value);
        _stringValue = value;
    }

    /// <summary>Gets whether this value represents an absent JSON-RPC ID property.</summary>
    public bool IsMissing => _kind == JsonRpcIdKind.Missing;

    /// <summary>Gets whether this value represents an explicit JSON null ID.</summary>
    public bool IsNull => _kind == JsonRpcIdKind.Null;

    internal static JsonRpcId FromValidatedRawStringToken(ReadOnlySpan<byte> rawToken)
    {
        if (rawToken.IsEmpty)
        {
            ThrowEmptyRawStringToken();
        }

        return new JsonRpcId(rawToken.ToArray());

        [DoesNotReturn, StackTraceHidden]
        static void ThrowEmptyRawStringToken() =>
            throw new JsonException("Expected JSON-RPC string ID token.");
    }

    internal static JsonRpcId FromValidatedRawDecimalToken(ReadOnlySpan<byte> rawToken, decimal value)
        => FromValidatedRawDecimalToken(rawToken.ToArray(), value);

    internal static JsonRpcId FromValidatedRawDecimalToken(byte[] rawToken, decimal value)
    {
        if (rawToken.Length == 0 || value.Scale != 0)
        {
            ThrowInvalidRawDecimalToken();
        }

        return new JsonRpcId(JsonRpcIdKind.Decimal, rawToken, value);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidRawDecimalToken() =>
            throw new JsonException("Expected JSON-RPC integer decimal ID token.");
    }

    /// <summary>Converts the legacy boxed ID representation into a typed JSON-RPC ID.</summary>
    /// <param name="value">The legacy ID value.</param>
    /// <returns>The equivalent typed ID.</returns>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="value"/> is not a supported JSON-RPC ID type.</exception>
    internal static JsonRpcId FromObject(object? value)
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

    /// <summary>Converts this typed ID to the legacy boxed representation.</summary>
    /// <returns>The boxed ID value, or null for missing and explicit-null IDs.</returns>
    internal object? ToObject()
    {
        return _kind switch
        {
            JsonRpcIdKind.Missing or JsonRpcIdKind.Null => null,
            JsonRpcIdKind.Long => _longValue,
            JsonRpcIdKind.Decimal => _decimalValue,
            JsonRpcIdKind.String => GetStringValue(),
            _ => ThrowInvalidKindObject()
        };

        [DoesNotReturn, StackTraceHidden]
        static object ThrowInvalidKindObject() =>
            throw new NotSupportedException("Unsupported JSON-RPC ID kind.");
    }

    /// <summary>Writes this ID as a JSON value.</summary>
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
                if (_rawValue is not null)
                {
                    writer.WriteRawValue(_rawValue, skipInputValidation: true);
                    break;
                }

                writer.WriteNumberValue(_decimalValue);
                break;
            case JsonRpcIdKind.String:
                if (_rawValue is null)
                {
                    ThrowMissingStringToken();
                }

                writer.WriteRawValue(_rawValue, skipInputValidation: true);
                break;
            default:
                ThrowInvalidKind();
                break;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMissingStringToken() =>
            throw new NotSupportedException("JSON-RPC string ID is missing its raw token.");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidKind() =>
            throw new NotSupportedException("Unsupported JSON-RPC ID kind.");
    }

    /// <summary>Tries to get this ID as a signed 64-bit integer.</summary>
    /// <param name="value">The integer ID value when this method returns true.</param>
    /// <returns>True when this ID stores a signed 64-bit integer; otherwise false.</returns>
    public bool TryGetInt64(out long value)
    {
        value = _longValue;
        return _kind == JsonRpcIdKind.Long;
    }

    /// <summary>Tries to get this ID as a decimal integer.</summary>
    /// <param name="value">The decimal ID value when this method returns true.</param>
    /// <returns>True when this ID stores a decimal integer; otherwise false.</returns>
    public bool TryGetDecimal(out decimal value)
    {
        value = _decimalValue;
        return _kind == JsonRpcIdKind.Decimal;
    }

    /// <inheritdoc/>
    public bool Equals(JsonRpcId other) => Equals(in other);

    /// <summary>Determines whether this ID and <paramref name="other"/> represent the same JSON-RPC ID.</summary>
    /// <param name="other">The ID to compare with this instance.</param>
    /// <returns>True when both IDs are equal; otherwise false.</returns>
    public bool Equals(in JsonRpcId other) =>
        _kind == other._kind &&
        _kind switch
        {
            JsonRpcIdKind.Long => _longValue == other._longValue,
            JsonRpcIdKind.Decimal => _decimalValue == other._decimalValue,
            JsonRpcIdKind.String => _rawValue.AsSpan().SequenceEqual(other._rawValue) ||
                string.Equals(GetStringValue(), other.GetStringValue(), StringComparison.Ordinal),
            _ => true
        };

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj switch
        {
            null => false,
            JsonRpcId other => Equals(in other),
            int intValue => _kind == JsonRpcIdKind.Long && _longValue == intValue,
            long longValue => _kind == JsonRpcIdKind.Long && _longValue == longValue,
            decimal decimalValue => _kind == JsonRpcIdKind.Decimal && _decimalValue == decimalValue,
            string stringValue => _kind == JsonRpcIdKind.String && string.Equals(GetStringValue(), stringValue, StringComparison.Ordinal),
            _ => false
        };

    /// <inheritdoc/>
    public override int GetHashCode() =>
        _kind switch
        {
            JsonRpcIdKind.Long => HashCode.Combine(_kind, _longValue),
            JsonRpcIdKind.Decimal => HashCode.Combine(_kind, _decimalValue),
            JsonRpcIdKind.String => HashCode.Combine(_kind, StringComparer.Ordinal.GetHashCode(GetStringValue())),
            _ => _kind.GetHashCode()
        };

    /// <summary>Returns a diagnostic representation of the ID.</summary>
    /// <returns>The ID as text, or a marker for missing/null states.</returns>
    public override string ToString() =>
        _kind switch
        {
            JsonRpcIdKind.Missing => "<missing>",
            JsonRpcIdKind.Null => "null",
            JsonRpcIdKind.Long => _longValue.ToString(CultureInfo.InvariantCulture),
            JsonRpcIdKind.Decimal => _decimalValue.ToString(CultureInfo.InvariantCulture),
            JsonRpcIdKind.String => GetStringValue(),
            _ => string.Empty
        };

    /// <summary>Converts an integer to a JSON-RPC ID.</summary>
    /// <param name="value">The integer value.</param>
    public static implicit operator JsonRpcId(int value) => new(value);

    /// <summary>Converts a signed 64-bit integer to a JSON-RPC ID.</summary>
    /// <param name="value">The integer value.</param>
    public static implicit operator JsonRpcId(long value) => new(value);

    /// <summary>Converts an integer decimal to a JSON-RPC ID.</summary>
    /// <param name="value">The decimal value.</param>
    public static implicit operator JsonRpcId(decimal value) => new(value);

    /// <summary>Converts a string to a JSON-RPC ID.</summary>
    /// <param name="value">The string value.</param>
    public static implicit operator JsonRpcId(string value) => new(value);

    private string GetStringValue()
    {
        string? stringValue = _stringValue;
        return stringValue is not null ? stringValue : ThrowMissingStringToken();

        [DoesNotReturn, StackTraceHidden]
        static string ThrowMissingStringToken() =>
            throw new NotSupportedException("JSON-RPC string ID is missing its raw token.");
    }

    private JsonRpcId(byte[] rawValue)
    {
        _kind = JsonRpcIdKind.String;
        _rawValue = rawValue;
        _stringValue = JsonSerializer.Deserialize<string>(rawValue);
    }

    private JsonRpcId(JsonRpcIdKind kind, byte[] rawValue, decimal decimalValue)
    {
        _kind = kind;
        _rawValue = rawValue;
        _decimalValue = decimalValue;
    }
}
