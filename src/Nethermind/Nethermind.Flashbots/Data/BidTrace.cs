// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Flashbots.Data;

using System.Text.Json.Serialization;

public class BidTrace(
    ulong slot,
    Hash256 parentHash,
    Hash256 blockHash,
    PublicKey builderPublicKey,
    PublicKey proposerPublicKey,
    Address proposerFeeRecipient,
    long gasLimit,
    long gasUsed,
    UInt256 value)
{
    [JsonPropertyName("slot")]
    public ulong Slot { get; set; } = slot;

    [JsonPropertyName("parent_hash")]
    public Hash256 ParentHash { get; set; } = parentHash;

    [JsonPropertyName("block_hash")]
    public Hash256 BlockHash { get; set; } = blockHash;

    [JsonPropertyName("builder_public_key")]
    [JsonConverter(typeof(PublicKeyConverter))]
    public PublicKey BuilderPublicKey { get; set; } = builderPublicKey;

    [JsonPropertyName("proposer_public_key")]
    [JsonConverter(typeof(PublicKeyConverter))]
    public PublicKey ProposerPublicKey { get; set; } = proposerPublicKey;

    [JsonPropertyName("proposer_fee_recipient")]
    public Address ProposerFeeRecipient { get; set; } = proposerFeeRecipient;

    [JsonPropertyName("gas_limit")]
    public long GasLimit { get; set; } = gasLimit;

    [JsonPropertyName("gas_used")]
    public long GasUsed { get; set; } = gasUsed;

    [JsonPropertyName("value")]
    public UInt256 Value { get; set; } = value;
}
