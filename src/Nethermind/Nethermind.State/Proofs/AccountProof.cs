// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style account proof
    /// </summary>
    ///
    [JsonConverter(typeof(ProofJsonConverter))]
    public class AccountProof
    {
        public Address? Address { get; set; }

        public byte[][]? Proof { get; set; }

        public UInt256 Balance { get; set; }

        public Hash256 CodeHash { get; set; } = Keccak.OfAnEmptyString;

        public ulong Nonce { get; set; }

        public Hash256 StorageRoot { get; set; } = Keccak.EmptyTreeHash;

        public StorageProof[]? StorageProofs { get; set; }
    }

    /// <summary>
    ///{
    ///  "id": 1,
    ///  "jsonrpc": "2.0",
    ///  "method": "eth_getProof",
    ///  "params": [
    ///    "0x7F0d15C7FAae65896648C8273B6d7E43f58Fa842",
    ///    [  "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421" ],
    ///    "latest"
    ///  ]
    ///}
    ///
    ///{
    ///  "id": 1,
    ///  "jsonrpc": "2.0",
    ///  "result": {
    ///    "accountProof": [
    ///    "0xf90211a...0701bc80",
    ///    "0xf90211a...0d832380",
    ///    "0xf90211a...5fb20c80",
    ///    "0xf90211a...0675b80",
    ///    "0xf90151a0...ca08080"
    ///    ],
    ///  "balance": "0x0",
    ///  "codeHash": "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470",
    ///  "nonce": "0x0",
    ///  "storageHash": "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421",
    ///  "storageProof": [
    ///  {
    ///    "key": "0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421",
    ///    "proof": [
    ///    "0xf90211a...0701bc80",
    ///    "0xf90211a...0d832380"
    ///    ],
    ///    "value": "0x1"
    ///  }
    ///  ]
    ///  }
    ///}
    /// </summary>
    public class ProofJsonConverter : JsonConverter<AccountProof>
    {
        // Resolved once from the options on first use. The registered converters are stateless,
        // process-wide singletons, so caching the first-seen instances is safe.
        private static JsonConverter<Address>? _addressConverter;
        private static JsonConverter<UInt256>? _uint256Converter;
        private static JsonConverter<Hash256>? _hashConverter;
        private static JsonConverter<ulong>? _ulongConverter;

        public override AccountProof Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            AccountProof value,
            JsonSerializerOptions options)
        {
            JsonConverter<Address> addressConverter = _addressConverter ??= (JsonConverter<Address>)options.GetConverter(typeof(Address));
            JsonConverter<UInt256> uint256Converter = _uint256Converter ??= (JsonConverter<UInt256>)options.GetConverter(typeof(UInt256));
            JsonConverter<Hash256> hashConverter = _hashConverter ??= (JsonConverter<Hash256>)options.GetConverter(typeof(Hash256));
            JsonConverter<ulong> ulongConverter = _ulongConverter ??= (JsonConverter<ulong>)options.GetConverter(typeof(ulong));

            writer.WriteStartObject();

            writer.WritePropertyName("accountProof"u8);
            JsonSerializer.Serialize(writer, value.Proof, options);

            writer.WritePropertyName("address"u8);
            addressConverter.Write(writer, value.Address, options);

            writer.WritePropertyName("balance"u8);
            uint256Converter.Write(writer, value.Balance, options);

            writer.WritePropertyName("codeHash"u8);
            hashConverter.Write(writer, value.CodeHash, options);

            writer.WritePropertyName("nonce"u8);
            ulongConverter.Write(writer, value.Nonce, options);

            writer.WritePropertyName("storageHash"u8);
            hashConverter.Write(writer, value.StorageRoot, options);

            writer.WritePropertyName("storageProof"u8);
            JsonSerializer.Serialize(writer, value.StorageProofs, options);

            writer.WriteEndObject();
        }
    }
}
