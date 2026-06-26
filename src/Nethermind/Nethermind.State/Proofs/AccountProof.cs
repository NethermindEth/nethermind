// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.State.Proofs
{
    /// <summary>
    /// EIP-1186 style account proof
    /// </summary>
    ///
    [JsonConverter(typeof(ProofJsonConverter))]
    public class AccountProof
    {
        public Address Address { get; set; } = null!;

        public byte[][] Proof { get; set; } = [];

        public UInt256 Balance { get; set; }

        public Hash256 CodeHash { get; set; } = Keccak.OfAnEmptyString;

        public ulong Nonce { get; set; }

        public Hash256 StorageRoot { get; set; } = Keccak.EmptyTreeHash;

        public StorageProof[] StorageProofs { get; set; } = [];
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
        private static readonly AddressConverter _addressConverter = new();
        private static readonly UInt256Converter _uint256Converter = new();
        private static readonly Hash256Converter _hashConverter = new();
        private static readonly ULongConverter _ulongConverter = new();

        public override AccountProof Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            AccountProof value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("accountProof"u8);
            JsonSerializer.Serialize(writer, value.Proof, options);

            writer.WritePropertyName("address"u8);
            _addressConverter.Write(writer, value.Address!, options);

            writer.WritePropertyName("balance"u8);
            _uint256Converter.Write(writer, value.Balance, options);

            writer.WritePropertyName("codeHash"u8);
            _hashConverter.Write(writer, value.CodeHash, options);

            writer.WritePropertyName("nonce"u8);
            _ulongConverter.Write(writer, value.Nonce, options);

            writer.WritePropertyName("storageHash"u8);
            _hashConverter.Write(writer, value.StorageRoot, options);

            writer.WritePropertyName("storageProof"u8);
            JsonSerializer.Serialize(writer, value.StorageProofs, options);

            writer.WriteEndObject();
        }
    }
}
