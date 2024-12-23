// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Flashbots.Data;

public class BidTrace(
    ulong slot,
    Hash256 blockHash,
    PublicKey builderPublicKey,
    PublicKey proposerPublicKey,
    Address proposerFeeRecipient,
    long gasLimit,
    long gasUsed,
    UInt256 value)
{
    public ulong Slot { get; } = slot;
    public required Hash256 ParentHash { get; set; }
    public Hash256 BlockHash { get; } = blockHash;
    public PublicKey BuilderPublicKey { get; } = builderPublicKey;
    public PublicKey ProposerPublicKey { get; } = proposerPublicKey;
    public Address ProposerFeeRecipient { get; } = proposerFeeRecipient;
    public long GasLimit { get; } = gasLimit;
    public long GasUsed { get; } = gasUsed;
    public UInt256 Value { get; } = value;
}
