// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Trace
{
    // An unknown member is invalid params, as in Parity, Reth and Besu, rather than a filter that silently ignores it.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public class TraceFilterForRpc : IJsonOnDeserialized
    {
        // Range bounds are block numbers or tags. A hash would only name a height, so one block is selected by blockHash.
        [JsonConverter(typeof(BlockNumberOrTagConverter))]
        public BlockParameter? FromBlock { get; set; }

        [JsonConverter(typeof(BlockNumberOrTagConverter))]
        public BlockParameter? ToBlock { get; set; }

        /// <summary>
        /// Selects exactly this block, which must be canonical, as <c>blockHash</c> does in eth_getLogs (EIP-234).
        /// </summary>
        [JsonConverter(typeof(NonEmptyHash256Converter))]
        public Hash256? BlockHash { get; set; }

        public Address[]? FromAddress { get; set; }

        public Address[]? ToAddress { get; set; }

        public TraceFilterMode Mode { get; set; }

        // after and count are JSON integers, not QUANTITYs, so the strict hex format must not reject a number here.
        [JsonConverter(typeof(NullableULongConverter))]
        public ulong? After { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        [JsonConverter(typeof(NullableULongConverter))]
        public ulong? Count { get; set; }

        /// <summary>
        /// The first and last block to trace: the block named by <see cref="BlockHash"/>, or the bounds, which default to latest.
        /// </summary>
        public (BlockParameter FromBlock, BlockParameter ToBlock) GetBlockRange()
        {
            if (BlockHash is null)
            {
                return (FromBlock ?? BlockParameter.Latest, ToBlock ?? BlockParameter.Latest);
            }

            BlockParameter block = new(BlockHash, requireCanonical: true);
            return (block, block);
        }

        // A null member is omitted, so a null bound may accompany a block hash.
        void IJsonOnDeserialized.OnDeserialized()
        {
            if (BlockHash is not null && (FromBlock is not null || ToBlock is not null))
            {
                throw new BlockParameterParseException("cannot specify both BlockHash and FromBlock/ToBlock, choose one or the other");
            }
        }
    }
}
