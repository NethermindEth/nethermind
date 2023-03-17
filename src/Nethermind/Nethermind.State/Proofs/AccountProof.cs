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
    [System.Text.Json.Serialization.JsonConverter(typeof(ProofJsonConverter))]
    public class AccountProof
    {
        public Address? Address { get; set; }

        public byte[][]? Proof { get; set; }

        public UInt256 Balance { get; set; }

        public Keccak CodeHash { get; set; } = Keccak.OfAnEmptyString;

        public UInt256 Nonce { get; set; }

        public Keccak StorageRoot { get; set; } = Keccak.EmptyTreeHash;

        public StorageProof[]? StorageProofs { get; set; }
    }


    public class ProofJsonConverter : JsonConverter<AccountProof>
    {
        public override AccountProof Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            AccountProof value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("accountProof"u8);
            JsonSerializer.Serialize(writer, value.Proof, options);

            var addressConverter = (JsonConverter<Address>)options.GetConverter(typeof(Address));
            writer.WritePropertyName("address"u8);
            addressConverter.Write(writer, value.Address, options);

            var uint256Converter = (JsonConverter<UInt256>)options.GetConverter(typeof(UInt256));
            writer.WritePropertyName("balance"u8);
            uint256Converter.Write(writer, value.Balance, options);

            var keccakConverter = (JsonConverter<Keccak>)options.GetConverter(typeof(Keccak));
            writer.WritePropertyName("codeHash"u8);
            keccakConverter.Write(writer, value.CodeHash, options);

            writer.WritePropertyName("nonce"u8);
            uint256Converter.Write(writer, value.Nonce, options);

            writer.WritePropertyName("storageHash"u8);
            keccakConverter.Write(writer, value.StorageRoot, options);

            writer.WritePropertyName("storageProof"u8);
            JsonSerializer.Serialize(writer, value.StorageProofs, options);

            writer.WriteEndObject();

        }
    }
}
