// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

[JsonConverter(typeof(GethTraceOptionsConverter))]
public record GethTraceOptions
{
    // Setters rather than init: source-generated metadata assigns every init-only member, defaulting absent ones, which would
    // let one of these aliases reset the other; setters apply only the members present, in JSON order.
    [Obsolete("Use EnableMemory instead.")]
    public bool DisableMemory { get => !EnableMemory; set => EnableMemory = !value; }

    public bool DisableStorage { get; init; }

    public bool EnableMemory { get; set; }

    public bool EnableReturnData { get; init; }

    public bool DisableStack { get; init; }

    /// <summary>
    /// Byte budget for opcode logs. Buffered traces use a conservative estimate; streamed traces count emitted bytes.
    /// Zero is unlimited; a negative value suppresses all logs.
    /// The entry that exceeds the budget is included. Execution and named tracers are unaffected.
    /// </summary>
    /// <remarks>Buffered traces can stop earlier than Geth or streamed traces, especially for repeated storage accesses
    /// and small stack values. Storage updates are counted without tracking distinct slots for the estimate.
    /// File budgets count JSON Lines records including each terminating line feed.</remarks>
    [JsonConverter(typeof(LimitConverter))]
    public long Limit { get; init; }

    private TimeSpan? _timeout;
    internal string? TimeoutText { get; init; }
    internal CancellationToken? ExecutionCancellation { get; init; }

    public TimeSpan? Timeout
    {
        get => TimeoutText is { } text ? GoTraceDuration.Parse(text) : _timeout;
        init { _timeout = value; TimeoutText = null; }
    }

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
    /// Where the callTracer's <c>withLog</c> indexes start and how they carry across transactions.
    /// </summary>
    /// <remarks>
    /// Set by the tracing entry points, never by the caller: one instance shared across a sequential block replay,
    /// an independent receipt-seeded instance per transaction otherwise, and <c>null</c>, numbering from zero,
    /// for a synthetic call.
    /// </remarks>
    [JsonIgnore]
    public BlockLogIndex? LogIndex { get; init; }

    /// <summary>
    /// When set, overrides <c>JsonRpc.EnableTracingStreamMode</c> for this single call.
    /// </summary>
    public bool? StreamMode { get; init; }

    public static GethTraceOptions Default => new();

    /// <summary>Reads a transaction index as an unsigned hexadecimal JSON quantity.</summary>
    public sealed class TransactionIndexConverter : JsonConverter<ulong>
    {
        /// <inheritdoc/>
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) ThrowInvalidToken();
            if (!reader.HasValueSequence && !reader.ValueIsEscaped) return ParseQuantity(reader.ValueSpan);
            const int maxQuantityLength = 18;
            const int maxEscapedLength = maxQuantityLength * 6;
            long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (length > maxEscapedLength) ThrowInvalidQuantity();
            Span<byte> buffer = stackalloc byte[maxEscapedLength];
            return ParseQuantity(buffer[..reader.CopyString(buffer)]);
        }

        private static ulong ParseQuantity(ReadOnlySpan<byte> value)
        {
            const int maxQuantityLength = 18;
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
        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => Nethermind.Serialization.Json.HexWriter.WriteUlongHexStringValue(writer, value);
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
                JsonTokenType.Number when reader.TryGetInt64(out long limit) => limit,
                _ => throw new JsonException("Trace limit must be a 64-bit integer.")
            };

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }
}
