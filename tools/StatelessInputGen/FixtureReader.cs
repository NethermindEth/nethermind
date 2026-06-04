// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.StatelessInputGen;

/// <summary>
/// Reads a <c>StatelessValidationFixture</c> JSON (as produced by
/// <c>witness-generator-cli</c> in the zkevm-benchmark-test repo) and
/// reconstructs the <see cref="Block"/> + <see cref="Witness"/> + chain id
/// triple needed by <see cref="Nethermind.Stateless.Execution.InputSerializer"/>.
///
/// Schema (alloy serde):
/// <code>
/// {
///   "name": "...",
///   "stateless_input": {
///     "block": {
///       "header": { camelCase fields ... },
///       "body": { "transactions": [...], "ommers": [...], "withdrawals": [...] }
///     },
///     "witness": { "state": [hex...], "codes": [...], "keys": [...], "headers": [...] },
///     "chain_config": { "chain_id": N, snake_case fork fields ... }
///   },
///   "success": true
/// }
/// </code>
///
/// Fixtures with <c>"success": false</c> are rejected; only post-merge blocks
/// (with no ommers) are supported. The Witness <c>keys</c> field is dropped
/// (InputSerializer ignores it).
/// </summary>
internal static class FixtureReader
{
    /// <summary>
    /// Parses a fixture file and returns the reconstructed (<see cref="Block"/>,
    /// <see cref="Witness"/>, chain id) triple, optionally accompanied by a
    /// <c>{"config": ...}</c> JSON envelope built from <c>stateless_input.chain_config</c>.
    /// </summary>
    /// <remarks>
    /// Building the envelope inline lets callers avoid re-opening and re-parsing
    /// the fixture; <see cref="BuildChainConfigEnvelope"/> documents the byte-for-byte
    /// compatibility with the Rust encoder.
    /// </remarks>
    internal static (Block Block, Witness Witness, ulong ChainId, byte[]? ChainConfigEnvelope, System.Text.Json.JsonElement ChainConfigCopy) Read(
        string path, bool includeChainConfigEnvelope = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fixture not found: {path}", path);

        using FileStream fs = File.OpenRead(path);
        using JsonDocument doc = JsonDocument.Parse(fs);

        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("success", out JsonElement successEl)
            && successEl.ValueKind == JsonValueKind.False)
            throw new InvalidDataException(
                $"Fixture '{path}' represents a failed scenario (success=false); refusing to build input from it.");

        JsonElement stateless = root.GetProperty("stateless_input");
        JsonElement blockJson = stateless.GetProperty("block");
        JsonElement witnessJson = stateless.GetProperty("witness");
        JsonElement chainConfig = stateless.GetProperty("chain_config");

        ulong chainId = chainConfig.GetProperty("chain_id").GetUInt64();

        BlockHeader header = ReadHeader(blockJson.GetProperty("header"));
        JsonElement bodyJson = blockJson.GetProperty("body");
        Block block = BuildBlock(header, bodyJson);
        Witness witness = ReadWitness(witnessJson);

        byte[]? envelope = includeChainConfigEnvelope ? BuildChainConfigEnvelope(chainConfig) : null;

        return (block, witness, chainId, envelope, chainConfig.Clone());
    }

    /// <summary>
    /// Wraps <paramref name="chainConfig"/> in <c>{"config": ...}</c> with snake_case
    /// keys converted to camelCase. Produces bytes byte-for-byte compatible with
    /// <c>serde_json::to_vec(&amp;json!({ "config": &amp;chain_config }))</c> on the
    /// Rust encoder side (alloy ChainConfig serializes as camelCase by default).
    /// </summary>
    private static byte[] BuildChainConfigEnvelope(JsonElement chainConfig)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("config");
            WriteCamelCase(writer, chainConfig);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Re-emits a JSON value with snake_case keys converted to camelCase,
    /// in <em>alphabetical key order</em>, dropping <c>null</c> values.
    ///
    /// Matches the byte output of Rust's
    /// <c>serde_json::to_vec(&amp;json!({ "config": &amp;chain_config }))</c>:
    /// the <c>json!</c> macro builds <c>serde_json::Value::Object</c> which
    /// stores keys in a <c>BTreeMap</c> (alphabetical), and alloy's
    /// <c>ChainConfig</c> serializer uses <c>skip_serializing_if = Option::is_none</c>.
    /// </summary>
    private static void WriteCamelCase(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Collect, rename, sort, skip nulls. StringComparer.Ordinal matches
                // Rust's BTreeMap<String,_> key ordering.
                List<KeyValuePair<string, JsonElement>> kv = [];
                foreach (JsonProperty prop in value.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                        continue;
                    kv.Add(new KeyValuePair<string, JsonElement>(SnakeToCamel(prop.Name), prop.Value));
                }
                kv.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
                foreach (KeyValuePair<string, JsonElement> entry in kv)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteCamelCase(writer, entry.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                    WriteCamelCase(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string SnakeToCamel(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.Contains('_'))
            return name;

        Span<char> buf = stackalloc char[name.Length];
        int o = 0;
        bool upper = false;
        foreach (char c in name)
        {
            if (c == '_') { upper = true; continue; }
            buf[o++] = upper ? char.ToUpperInvariant(c) : c;
            upper = false;
        }
        return new string(buf[..o]);
    }

    private static BlockHeader ReadHeader(JsonElement h)
    {
        Hash256 parentHash = ReadHash(h, "parentHash");
        Hash256 unclesHash = ReadHash(h, "sha3Uncles");
        Address beneficiary = ReadAddress(h, "miner");
        UInt256 difficulty = ReadUInt256Hex(h, "difficulty");
        long number = ReadInt64Hex(h, "number");
        long gasLimit = ReadInt64Hex(h, "gasLimit");
        ulong timestamp = ReadUInt64Hex(h, "timestamp");
        byte[] extraData = ReadBytes(h, "extraData");

        BlockHeader header = new(
            parentHash,
            unclesHash,
            beneficiary,
            in difficulty,
            number,
            gasLimit,
            timestamp,
            extraData);

        header.GasUsed = ReadInt64Hex(h, "gasUsed");
        header.StateRoot = ReadHash(h, "stateRoot");
        header.TxRoot = ReadHash(h, "transactionsRoot");
        header.ReceiptsRoot = ReadHash(h, "receiptsRoot");
        header.Bloom = ReadBloom(h, "logsBloom");
        header.MixHash = ReadHash(h, "mixHash");
        header.Nonce = ReadUInt64Hex(h, "nonce");

        if (h.TryGetProperty("baseFeePerGas", out JsonElement baseFee) && baseFee.ValueKind != JsonValueKind.Null)
            header.BaseFeePerGas = ParseUInt256(baseFee.GetString()!);

        if (h.TryGetProperty("withdrawalsRoot", out JsonElement wr) && wr.ValueKind != JsonValueKind.Null)
            header.WithdrawalsRoot = new Hash256(wr.GetString()!);

        if (h.TryGetProperty("parentBeaconBlockRoot", out JsonElement pbr) && pbr.ValueKind != JsonValueKind.Null)
            header.ParentBeaconBlockRoot = new Hash256(pbr.GetString()!);

        if (h.TryGetProperty("requestsHash", out JsonElement rh) && rh.ValueKind != JsonValueKind.Null)
            header.RequestsHash = new Hash256(rh.GetString()!);

        if (h.TryGetProperty("blobGasUsed", out JsonElement bgu) && bgu.ValueKind != JsonValueKind.Null)
            header.BlobGasUsed = ParseUInt64(bgu.GetString()!);

        if (h.TryGetProperty("excessBlobGas", out JsonElement ebg) && ebg.ValueKind != JsonValueKind.Null)
            header.ExcessBlobGas = ParseUInt64(ebg.GetString()!);

        if (h.TryGetProperty("blockAccessListHash", out JsonElement balh) && balh.ValueKind != JsonValueKind.Null)
            header.BlockAccessListHash = new Hash256(balh.GetString()!);

        return header;
    }

    private static Block BuildBlock(BlockHeader header, JsonElement bodyJson)
    {
        if (bodyJson.TryGetProperty("ommers", out JsonElement ommers)
            && ommers.ValueKind == JsonValueKind.Array
            && ommers.GetArrayLength() > 0)
            throw new NotSupportedException(
                "Fixtures with ommers (pre-merge blocks) are not supported by the stateless executor.");

        JsonElement txsJson = bodyJson.GetProperty("transactions");
        Transaction[] transactions = new Transaction[txsJson.GetArrayLength()];
        int txIdx = 0;
        foreach (JsonElement tx in txsJson.EnumerateArray())
            transactions[txIdx++] = TransactionReader.Read(tx);

        Withdrawal[]? withdrawals = null;
        if (bodyJson.TryGetProperty("withdrawals", out JsonElement wsJson) && wsJson.ValueKind == JsonValueKind.Array)
        {
            withdrawals = new Withdrawal[wsJson.GetArrayLength()];
            int i = 0;
            foreach (JsonElement w in wsJson.EnumerateArray())
                withdrawals[i++] = new Withdrawal
                {
                    Index = ParseUInt64(w.GetProperty("index").GetString()!),
                    ValidatorIndex = ParseUInt64(w.GetProperty("validatorIndex").GetString()!),
                    Address = new Address(w.GetProperty("address").GetString()!),
                    AmountInGwei = ParseUInt64(w.GetProperty("amount").GetString()!),
                };
        }

        return new Block(header, transactions, [], withdrawals);
    }

    private static Witness ReadWitness(JsonElement w) => new()
    {
        Codes = ReadByteArrayList(w.GetProperty("codes")),
        Headers = ReadByteArrayList(w.GetProperty("headers")),
        // InputSerializer ignores keys but Witness requires the field to be set.
        Keys = ArrayPoolList<byte[]>.Empty(),
        State = ReadByteArrayList(w.GetProperty("state")),
    };

    private static ArrayPoolList<byte[]> ReadByteArrayList(JsonElement arr)
    {
        int count = arr.GetArrayLength();
        ArrayPoolList<byte[]> list = new(count, count);
        int i = 0;
        foreach (JsonElement item in arr.EnumerateArray())
            list[i++] = ParseHex(item.GetString()!);
        return list;
    }

    private static Hash256 ReadHash(JsonElement parent, string field)
        => new(parent.GetProperty(field).GetString()!);

    private static Address ReadAddress(JsonElement parent, string field)
        => new(parent.GetProperty(field).GetString()!);

    private static long ReadInt64Hex(JsonElement parent, string field)
        => (long)ParseUInt64(parent.GetProperty(field).GetString()!);

    private static ulong ReadUInt64Hex(JsonElement parent, string field)
        => ParseUInt64(parent.GetProperty(field).GetString()!);

    private static UInt256 ReadUInt256Hex(JsonElement parent, string field)
        => ParseUInt256(parent.GetProperty(field).GetString()!);

    private static byte[] ReadBytes(JsonElement parent, string field)
        => ParseHex(parent.GetProperty(field).GetString()!);

    private static Bloom ReadBloom(JsonElement parent, string field)
        => new(ParseHex(parent.GetProperty(field).GetString()!));

    private static ulong ParseUInt64(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static UInt256 ParseUInt256(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        return UInt256.Parse(s, NumberStyles.HexNumber);
    }

    private static byte[] ParseHex(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        if (s.Length == 0) return [];
        // Convert.FromHexString throws a generic "must be an even number of characters"
        // FormatException without echoing the offending value; re-raise with the
        // original string so the operator can spot which field is malformed.
        if ((s.Length & 1) != 0)
            throw new FormatException($"Hex value '{hex}' has odd length; expected an even number of hex digits.");
        return Convert.FromHexString(s);
    }
}
