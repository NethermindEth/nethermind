// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Mev.Data;

public class BidTrace
{
    public ulong Slot { get; set; }
    public Keccak? ParentHash { get; set; }
    public Keccak? BlockHash { get; set; }
    public byte[]? BuilderPubkey { get; set; }
    public byte[]? ProposerPubkey { get; set; }
    public Address? ProposerFeeRecipient { get; set; }
    public long GasLimit { get; set; }
    public long GasUsed { get; set; }
    public UInt256 Value { get; set; }
}
