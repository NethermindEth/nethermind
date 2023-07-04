// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.State.Proofs;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
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
    public class ProofConverter : JsonConverter<AccountProof>
    {
        public override void WriteJson(JsonWriter writer, AccountProof value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("accountProof");
            writer.WriteStartArray();
            for (int i = 0; i < value.Proof.Length; i++)
            {
                writer.WriteValue(value.Proof[i].ToHexString(true));
            }
            writer.WriteEnd();
            writer.WriteProperty("address", value.Address, serializer);
            writer.WriteProperty("balance", value.Balance, serializer);
            writer.WriteProperty("codeHash", value.CodeHash, serializer);
            writer.WriteProperty("nonce", value.Nonce, serializer);
            writer.WriteProperty("storageHash", value.StorageRoot, serializer);
            writer.WritePropertyName("storageProof");
            writer.WriteStartArray();
            for (int i = 0; i < value.StorageProofs.Length; i++)
            {
                writer.WriteStartObject();
                writer.WriteProperty("key", value.StorageProofs[i].Key, serializer);
                writer.WritePropertyName("proof");
                writer.WriteStartArray();
                for (int ip = 0; ip < value.StorageProofs[i].Proof.Length; ip++)
                {
                    writer.WriteValue(value.StorageProofs[i].Proof[ip].ToHexString(true));
                }
                writer.WriteEnd();
                writer.WriteProperty("value", value.StorageProofs[i].Value, serializer);
                writer.WriteEnd();
            }
            writer.WriteEnd();
            writer.WriteEnd();

        }

        public override AccountProof ReadJson(JsonReader reader, Type objectType, AccountProof existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
