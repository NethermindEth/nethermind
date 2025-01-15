// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Flashbots.Data;

public class BidTrace
{
    public ulong Slot { get; set; }

    [JsonPropertyName("parent_hash")]
    [Newtonsoft.Json.JsonRequired]
    public required Hash256 ParentHash { get; set; }

    [JsonPropertyName("block_hash")]
    [Newtonsoft.Json.JsonRequired]
    public required Hash256 BlockHash { get; set; }

    [JsonPropertyName("builder_pubkey")]
    [Newtonsoft.Json.JsonRequired]
    public required PublicKey BuilderPublicKey { get; set; }


    [JsonPropertyName("proposer_pubkey")]
    [Newtonsoft.Json.JsonRequired]
    public required PublicKey ProposerPublicKey { get; set; }


    [JsonPropertyName("proposer_fee_recipient")]
    [Newtonsoft.Json.JsonRequired]
    public required Address ProposerFeeRecipient { get; set; }


    [JsonPropertyName("gas_limit")]
    public long GasLimit { get; set; }

    [JsonPropertyName("gas_used")]
    public long GasUsed { get; set; }

    public UInt256 Value { get; set; }
}
