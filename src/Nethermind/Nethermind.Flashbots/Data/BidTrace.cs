// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Flashbots.Data;

using System.Text.Json.Serialization;

public class BidTrace
{
    [JsonPropertyName("slot")]
    public ulong Slot { get; set; }

    [JsonPropertyName("parent_hash")]
    public Hash256 ParentHash { get; set; }

    [JsonPropertyName("block_hash")]
    public Hash256 BlockHash { get; set; }

    [JsonPropertyName("builder_public_key")]
    public PublicKey BuilderPublicKey { get; set; }

    [JsonPropertyName("proposer_public_key")]
    public PublicKey ProposerPublicKey { get; set; }

    [JsonPropertyName("proposer_fee_recipient")]
    public Address ProposerFeeRecipient { get; set; }

    [JsonPropertyName("gas_limit")]
    public long GasLimit { get; set; }

    [JsonPropertyName("gas_used")]
    public long GasUsed { get; set; }

    [JsonPropertyName("value")]
    public UInt256 Value { get; set; }

    public BidTrace(
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
        Slot = slot;
        ParentHash = parentHash;
        BlockHash = blockHash;
        BuilderPublicKey = builderPublicKey;
        ProposerPublicKey = proposerPublicKey;
        ProposerFeeRecipient = proposerFeeRecipient;
        GasLimit = gasLimit;
        GasUsed = gasUsed;
        Value = value;
    }
}
