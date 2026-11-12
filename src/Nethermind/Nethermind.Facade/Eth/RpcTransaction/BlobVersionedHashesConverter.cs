// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Eth.RpcTransaction;

internal sealed class BlobVersionedHashesConverter : ByteArrayArrayConverter
{
    public override byte[]?[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new SafePublicMessageFormatException("Expected blob versioned hashes array");

        const int encodedHashLength = 2 + 2 * Eip4844Constants.BytesPerBlobVersionedHash;
        Span<byte> buffer = stackalloc byte[encodedHashLength];
        using ArrayPoolListRef<byte[]> hashes = new(InitialEwma);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return hashes.ToArray();
            // Geth's common.Hash.UnmarshalJSON checks the quoted hex bytes without unescaping.
            if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped)
                throw new SafePublicMessageFormatException("Expected unescaped blob versioned hash hex string");

            long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (length > encodedHashLength)
                throw new SafePublicMessageFormatException(RpcTransactionErrors.InvalidBlobVersionedHashSize);

            ReadOnlySpan<byte> hash;
            if (reader.HasValueSequence)
            {
                reader.ValueSequence.CopyTo(buffer);
                hash = buffer[..(int)length];
            }
            else
            {
                hash = reader.ValueSpan;
            }

            if (hash.Length < 2 || hash[0] != (byte)'0' || (hash[1] != (byte)'x' && hash[1] != (byte)'X'))
                throw new SafePublicMessageFormatException("hex string without 0x prefix");
            if (hash.Length != encodedHashLength)
                throw new SafePublicMessageFormatException(RpcTransactionErrors.InvalidBlobVersionedHashSize);

            try
            {
                hashes.Add(Bytes.FromUtf8HexString(hash[2..]));
            }
            catch (FormatException exception)
            {
                throw new SafePublicMessageFormatException("Invalid blob versioned hash hex", exception);
            }
        }

        throw new SafePublicMessageFormatException("Unterminated blob versioned hashes array");
    }
}
