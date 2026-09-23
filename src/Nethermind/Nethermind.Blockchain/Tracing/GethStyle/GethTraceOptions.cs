// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public record GethTraceOptions
{
    [Obsolete("Use EnableMemory instead.")]
    public bool DisableMemory { get => !EnableMemory; init => EnableMemory = !value; }

    public bool DisableStorage { get; init; }

    public bool EnableMemory { get; init; }

    public bool EnableReturnData { get; init; }

    public bool DisableStack { get; init; }

    /// <summary>
    /// Byte limit for serialized opcode logs. Zero is unlimited; a negative value suppresses all logs.
    /// The entry that exceeds the limit is included. Execution and named tracers are unaffected.
    /// </summary>
    [JsonConverter(typeof(LimitConverter))]
    public long Limit { get; init; }

    [JsonConverter(typeof(CustomTimeDurationConverter))]
    public TimeSpan? Timeout { get; init; }

    public string Tracer { get; init; }

    public Hash256? TxHash { get; init; }

    /// <summary>For traceCall, selects state before this transaction in the requested block.</summary>
    [JsonConverter(typeof(TransactionIndexConverter))]
    public ulong? TxIndex { get; init; }

    public JsonElement? TracerConfig { get; init; }

    public Dictionary<Address, AccountOverride>? StateOverrides { get; init; }

    public BlockOverride? BlockOverrides { get; set; }

    [JsonIgnore]
    public bool NoBaseFee { get; init; }

    /// <summary>
    /// When set, overrides <c>JsonRpc.EnableTracingStreamMode</c> for this single call.
    /// </summary>
    public bool? StreamMode { get; init; }

    public static GethTraceOptions Default { get; } = new();

    /// <summary>Reads a transaction index as an unsigned hexadecimal JSON quantity.</summary>
    public sealed class TransactionIndexConverter : JsonConverter<ulong>
    {
        /// <inheritdoc/>
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) ThrowInvalidToken();
            const int maxQuantityLength = 18;
            const int maxEscapedLength = maxQuantityLength * 6;
            long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (length > maxEscapedLength) ThrowInvalidQuantity();
            Span<byte> buffer = stackalloc byte[maxEscapedLength];
            ReadOnlySpan<byte> value = buffer[..reader.CopyString(buffer)];
            if (value.Length is < 3 or > maxQuantityLength || value[0] != '0' || (value[1] != 'x' && value[1] != 'X')
                || (value.Length > 3 && value[2] == '0'))
                ThrowInvalidQuantity();
            if (!ulong.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong index))
                ThrowInvalidQuantity();
            return index;
        }

        [DoesNotReturn]
        private static void ThrowInvalidToken() => throw new JsonException("Transaction index must be a hex quantity string.");

        [DoesNotReturn]
        private static void ThrowInvalidQuantity() => throw new JsonException("Invalid transaction index hex quantity.");

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WriteStringValue($"0x{value:x}");
    }

    /// <summary>
    /// Reads a signed JSON integer or null for the opcode logger byte limit.
    /// </summary>
    public sealed class LimitConverter : JsonConverter<long>
    {
        /// <inheritdoc/>
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Null => 0,
                JsonTokenType.Number => reader.GetInt64(),
                _ => throw new JsonException("Trace limit must be an integer.")
            };

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }
}
