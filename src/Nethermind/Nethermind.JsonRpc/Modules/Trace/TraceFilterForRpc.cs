// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Json;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules.Trace
{
    // An unknown member is invalid params, as in Parity, Reth and Besu, rather than a filter that silently ignores it.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public class TraceFilterForRpc
    {
        public const string BlockHashBoundNotAllowed = "fromBlock and toBlock take a block number or tag, not a block hash; use blockHash to select a block by hash";
        public const string BlockHashWithRange = "cannot specify both blockHash and fromBlock/toBlock, choose one or the other";

        /// <remarks>A block hash is rejected, as in eth_getLogs: a range names blocks by number or tag, and <see cref="BlockHash"/> selects a single block.</remarks>
        [JsonConverter(typeof(NullAsOmittedConverter))]
        public BlockParameter? FromBlock { get; set => field = ValidateBound(value); }

        /// <inheritdoc cref="FromBlock"/>
        [JsonConverter(typeof(NullAsOmittedConverter))]
        public BlockParameter? ToBlock { get; set => field = ValidateBound(value); }

        /// <summary>Selects exactly the canonical block with this hash, as the EIP-234 member of eth_getLogs does.</summary>
        /// <remarks>Mutually exclusive with <see cref="FromBlock"/> and <see cref="ToBlock"/>.</remarks>
        public Hash256? BlockHash
        {
            get;
            set
            {
                if (value is not null && (FromBlock is not null || ToBlock is not null))
                    throw new SafePublicMessageFormatException(BlockHashWithRange);
                field = value;
            }
        }

        public Address[]? FromAddress { get; set; }

        public Address[]? ToAddress { get; set; }

        public TraceFilterMode Mode { get; set; }

        // after and count are JSON integers, not QUANTITYs, so the strict hex format must not reject a number here.
        [JsonConverter(typeof(NullableULongConverter))]
        public ulong? After { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        [JsonConverter(typeof(NullableULongConverter))]
        public ulong? Count { get; set; }

        private BlockParameter? ValidateBound(BlockParameter? bound)
        {
            if (bound is null) return null;
            if (bound.Type == BlockParameterType.BlockHash) throw new SafePublicMessageFormatException(BlockHashBoundNotAllowed);
            if (BlockHash is not null) throw new SafePublicMessageFormatException(BlockHashWithRange);
            return bound;
        }

        /// <summary>Reads a JSON null bound as omitted rather than as <c>latest</c>, so it does not conflict with <see cref="BlockHash"/>.</summary>
        private sealed class NullAsOmittedConverter : JsonConverter<BlockParameter?>
        {
            public override BlockParameter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                JsonSerializer.Deserialize<BlockParameter>(ref reader, options);

            public override void Write(Utf8JsonWriter writer, BlockParameter? value, JsonSerializerOptions options) =>
                JsonSerializer.Serialize(writer, value, options);
        }
    }
}
