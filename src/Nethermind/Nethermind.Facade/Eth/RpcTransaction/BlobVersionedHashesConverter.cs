// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

internal sealed class BlobVersionedHashesConverter : ByteArrayArrayConverter
{
    public override byte[]?[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected blob versioned hashes array");

        List<byte[]> hashes = [];
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return hashes.ToArray();
            // Geth's common.Hash.UnmarshalJSON checks the quoted hex bytes without unescaping.
            if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped)
                throw new JsonException("Expected unescaped blob versioned hash hex string");

            string hash = reader.GetString()!;
            if (hash.Length != 2 + 2 * Eip4844Constants.BytesPerBlobVersionedHash || !hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                throw new JsonException(RpcTransactionErrors.InvalidBlobVersionedHashSize);

            try
            {
                hashes.Add(Convert.FromHexString(hash.AsSpan(2)));
            }
            catch (FormatException exception)
            {
                throw new JsonException("Invalid blob versioned hash hex", exception);
            }
        }

        throw new JsonException("Unterminated blob versioned hashes array");
    }
}
