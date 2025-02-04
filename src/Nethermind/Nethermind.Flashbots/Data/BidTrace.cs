// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Flashbots.Data;

public class BidTrace
{
    public ulong Slot { get; }
    public Hash256 ParentHash { get; }
    public Hash256 BlockHash { get; }
    public PublicKey BuilderPublicKey { get; }
    public PublicKey ProposerPublicKey { get; }
    public Address ProposerFeeRecipient { get; }
    public long GasLimit { get; }
    public long GasUsed { get; }
    public UInt256 Value { get; }

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

    // public BidTrace(Message message) : this(
    //     message.Slot,
    //     message.ParentHash,
    //     message.BlockHash,
    //     message.BuilderPublicKey,
    //     message.ProposerPublicKey,
    //     message.ProposerFeeRecipient,
    //     message.GasLimit,
    //     message.GasUsed,
    //     message.Value)
    // {
    // }
}
