// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class TransactionJson
    {
        public TxType Type { get; set; }
        public Address Sender { get; set; }
        public byte[][]? Data { get; set; }
        public ulong[]? GasLimit { get; set; }
        [JsonConverter(typeof(SaturatingNullableUInt256Converter))]
        public UInt256? GasPrice { get; set; }
        [JsonConverter(typeof(SaturatingNullableUInt256Converter))]
        public UInt256? MaxFeePerGas { get; set; }
        [JsonConverter(typeof(SaturatingNullableUInt256Converter))]
        public UInt256? MaxPriorityFeePerGas { get; set; }
        [JsonConverter(typeof(SaturatingULongConverter))]
        public ulong Nonce { get; set; }
        public Address? To { get; set; }
        public UInt256[]? Value { get; set; }
        public byte[]? SecretKey { get; set; }
        public AccessListItemJson[]?[]? AccessLists { get; set; }
        public AccessListItemJson[]? AccessList { get; set; }
        public AuthorizationListJson[]? AuthorizationList { get; set; }
        public byte[]?[]? BlobVersionedHashes { get; set; }
        [JsonConverter(typeof(SaturatingNullableUInt256Converter))]
        public UInt256? MaxFeePerBlobGas { get; set; }
    }
}
