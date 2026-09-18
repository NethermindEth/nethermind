// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.TxPool;

/// <summary>The key a sender's transaction is listed under in <c>txpool_content</c>, <c>txpool_contentFrom</c> and
/// <c>txpool_inspect</c>: its nonce, or its hash when EIP-8250 lets several includable transactions share one
/// sequence number.</summary>
/// <remarks>Held as the values rather than the rendered text: <see cref="TxPoolTxKeyConverter"/> writes the JSON
/// property name straight as UTF-8, so listing the pool costs no string per transaction.</remarks>
[JsonConverter(typeof(TxPoolTxKeyConverter))]
public readonly struct TxPoolTxKey : IEquatable<TxPoolTxKey>
{
    /// <summary>A key rendered as the decimal <paramref name="nonce"/>.</summary>
    public TxPoolTxKey(ulong nonce) => Nonce = nonce;

    /// <summary>A key rendered as the <c>0x</c>-prefixed transaction <paramref name="hash"/>.</summary>
    public TxPoolTxKey(Hash256 hash) => Hash = hash;

    /// <summary>The transaction hash, or <see langword="null"/> when the key is a nonce.</summary>
    public Hash256? Hash { get; }

    /// <summary>The nonce, meaningful only while <see cref="Hash"/> is <see langword="null"/>.</summary>
    public ulong Nonce { get; }

    public bool Equals(TxPoolTxKey other) =>
        Hash is null ? other.Hash is null && Nonce == other.Nonce : Hash.Equals(other.Hash);

    public override bool Equals(object? obj) => obj is TxPoolTxKey other && Equals(other);

    public override int GetHashCode() => Hash?.GetHashCode() ?? Nonce.GetHashCode();

    public override string ToString() => Hash?.ToString() ?? Nonce.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reads and writes <see cref="TxPoolTxKey"/> as the JSON object key the <c>txpool_*</c> responses carry:
/// decimal digits for a nonce, lowercase <c>0x</c>-prefixed hex for a hash.</summary>
public sealed class TxPoolTxKeyConverter : JsonConverter<TxPoolTxKey>
{
    private const int HashNameLength = Hash256.Size * 2 + 2;

    public override TxPoolTxKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadCore(ref reader);

    public override TxPoolTxKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadCore(ref reader);

    public override void Write(Utf8JsonWriter writer, TxPoolTxKey value, JsonSerializerOptions options)
    {
        Span<byte> name = stackalloc byte[HashNameLength];
        writer.WriteStringValue(Format(in value, name));
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TxPoolTxKey value, JsonSerializerOptions options)
    {
        Span<byte> name = stackalloc byte[HashNameLength];
        writer.WritePropertyName(Format(in value, name));
    }

    private static ReadOnlySpan<byte> Format(in TxPoolTxKey key, Span<byte> destination)
    {
        Hash256? hash = key.Hash;
        if (hash is null)
        {
            key.Nonce.TryFormat(destination, out int written, provider: CultureInfo.InvariantCulture);
            return destination[..written];
        }

        destination[0] = (byte)'0';
        destination[1] = (byte)'x';
        ((ReadOnlySpan<byte>)hash.Bytes).OutputBytesToByteHex(destination[2..], false);
        return destination;
    }

    private static TxPoolTxKey ReadCore(ref Utf8JsonReader reader)
    {
        ReadOnlySpan<byte> name = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
        return name.Length == HashNameLength && name[0] == (byte)'0' && name[1] == (byte)'x'
            ? new TxPoolTxKey(new Hash256(Bytes.FromUtf8HexString(name[2..])))
            : new TxPoolTxKey(ulong.Parse(name, CultureInfo.InvariantCulture));
    }
}
