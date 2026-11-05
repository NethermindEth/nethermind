// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Reads one owned genesis document and preserves its original canonical identity.</summary>
internal static class NativeChainDocument
{
    private static readonly string[] GenesisFields = ["alloc", "baseFeePerGas", "blobGasUsed", "coinbase", "config", "difficulty",
        "excessBlobGas", "extraData", "gasLimit", "mixHash", "nonce", "number", "parentHash", "timestamp"];
    private static readonly string[] AccountFields = ["balance", "nonce", "code", "storage"];

    internal static (byte[] Normalized, ValueHash256 Digest) Read(ReadOnlyMemory<byte> input)
    {
        ReadOnlyMemory<byte> canonical = RollupXCanonicalJson.Encode(input.ToArray());
        JsonObject document = JsonNode.Parse(canonical.Span)?.AsObject() ?? throw new InvalidDataException("Missing execution genesis document.");
        if (document.TryGetPropertyValue("mixhash", out JsonNode? alias))
        {
            document.Remove("mixhash");
            if (document.TryGetPropertyValue("mixHash", out JsonNode? value))
            {
                if (!JsonNode.DeepEquals(value, alias)) throw new InvalidDataException("Genesis mixHash and mixhash disagree.");
            }
            else document["mixHash"] = alias;
        }
        return (JsonSerializer.SerializeToUtf8Bytes(document), ValueKeccak.Compute(canonical.Span));
    }

    internal static void ValidateGenesis(JsonElement genesis)
    {
        EnsureFields(genesis, GenesisFields);
        if (genesis.TryGetProperty("number", out JsonElement number) && Quantity(number) != 0)
            throw new InvalidDataException("Execution genesis number and parent must be zero.");
        if (genesis.TryGetProperty("parentHash", out JsonElement parent))
        {
            string? text = parent.GetString();
            byte[] bytes = text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Bytes.FromHexString(text) : [];
            if (bytes.Length != Hash256.Size || !bytes.AsSpan().IsZero())
                throw new InvalidDataException("Execution genesis parent must be a zero 32-byte hash.");
        }
        JsonElement allocations = Required(genesis, "alloc");
        if (allocations.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Genesis alloc must be an object.");
        HashSet<Address> addresses = new(allocations.GetPropertyCount());
        foreach (JsonProperty entry in allocations.EnumerateObject())
        {
            string name = entry.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? entry.Name : "0x" + entry.Name;
            if (!Address.TryParse(name, out Address? address) || address is null || !addresses.Add(address))
                throw new InvalidDataException("Invalid or duplicate genesis account.");
            EnsureFields(entry.Value, AccountFields);
            if (!entry.Value.TryGetProperty("storage", out JsonElement storage) || storage.ValueKind == JsonValueKind.Null) continue;
            if (storage.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Genesis storage must be an object.");
            HashSet<UInt256> keys = new(storage.GetPropertyCount());
            foreach (JsonProperty slot in storage.EnumerateObject())
            {
                if (!keys.Add(StorageWord(slot.Name))) throw new InvalidDataException("Duplicate genesis storage slot.");
                _ = StorageWord(slot.Value.GetString());
            }
        }
    }

    internal static void EnsureFields(JsonElement value, ReadOnlySpan<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a genesis configuration object.");
        foreach (JsonProperty property in value.EnumerateObject())
        {
            bool found = false;
            foreach (string field in fields)
                if (property.NameEquals(field)) { found = true; break; }
            if (!found) throw new InvalidDataException($"Unknown genesis key {property.Name}.");
        }
    }

    internal static JsonElement Required(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement field) && field.ValueKind != JsonValueKind.Null
            ? field : throw new InvalidDataException($"Missing genesis field {name}.");

    internal static UInt256 StorageWord(string? text)
    {
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Storage words must be hex values.");
        byte[] bytes = Bytes.FromHexString(text);
        if (bytes.Length > AbiWord.Size) throw new InvalidDataException("Storage words exceed 256 bits.");
        return new UInt256(bytes, isBigEndian: true);
    }

    internal static ulong Quantity(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)) return number;
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is not null && (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)
                : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            ? number : throw new InvalidDataException("Invalid unsigned genesis quantity.");
    }
}
