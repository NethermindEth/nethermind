// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Deneb <c>ExecutionPayloadHeader</c> (unchanged in Electra and Fulu).</summary>
[SszContainer]
public partial class ExecutionPayloadHeader
{
    public Hash256? ParentHash { get; set; }

    public Address? FeeRecipient { get; set; }

    public Hash256? StateRoot { get; set; }

    public Hash256? ReceiptsRoot { get; set; }

    public Bloom? LogsBloom { get; set; }

    public Hash256? PrevRandao { get; set; }

    public ulong BlockNumber { get; set; }

    public ulong GasLimit { get; set; }

    public ulong GasUsed { get; set; }

    public ulong Timestamp { get; set; }

    [SszList(32)]
    public byte[]? ExtraData { get; set; }

    public UInt256 BaseFeePerGas { get; set; }

    public Hash256? BlockHash { get; set; }

    public Hash256? TransactionsRoot { get; set; }

    public Hash256? WithdrawalsRoot { get; set; }

    public ulong BlobGasUsed { get; set; }

    public ulong ExcessBlobGas { get; set; }
}
