// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Flashbots.Data;

public class Message(
    ulong slot,
    Hash256 parent_hash,
    Hash256 block_hash,
    PublicKey builder_pubkey,
    PublicKey proposer_pubkey,
    Address proposer_fee_recipient,
    long gas_limit,
    long gas_used,
    UInt256 value)
{
    public ulong slot { get; } = slot;
    public Hash256 parent_hash { get; set; } = parent_hash;
    public Hash256 block_hash { get; } = block_hash;
    public PublicKey builder_pubkey { get; } = builder_pubkey;
    public PublicKey proposer_pubkey { get; } = proposer_pubkey;
    public Address proposer_fee_recipient { get; } = proposer_fee_recipient;
    public long gas_limit { get; } = gas_limit;
    public long gas_used { get; } = gas_used;
    public UInt256 value { get; } = value;

    public BidTrace ToBidTrace()
    {
        return new BidTrace(
            slot,
            parent_hash,
            block_hash,
            builder_pubkey,
            proposer_pubkey,
            proposer_fee_recipient,
            gas_limit,
            gas_used,
            value
        );
    }
}

