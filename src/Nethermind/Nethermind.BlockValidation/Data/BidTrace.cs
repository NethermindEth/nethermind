// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.BlockValidation.Data;

public readonly struct BidTrace 
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
}