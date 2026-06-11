// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Capella <c>Withdrawal</c>.</summary>
[SszContainer]
public partial class Withdrawal
{
    public ulong Index { get; set; }

    public ulong ValidatorIndex { get; set; }

    public Address? Address { get; set; }

    public ulong Amount { get; set; }
}

/// <summary>Bellatrix <c>Transaction</c>: ByteList[<c>MAX_BYTES_PER_TRANSACTION</c> = 2**30].</summary>
[SszContainer(isCollectionItself: true)]
public partial class Transaction
{
    [SszList(1_073_741_824)]
    public byte[]? Bytes { get; set; }
}

/// <summary>Deneb <c>ExecutionPayload</c> (unchanged in Electra and Fulu).</summary>
[SszContainer]
public partial class ExecutionPayload
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

    [SszList(1_048_576)]
    public Transaction[]? Transactions { get; set; }

    [SszList(16)]
    public Withdrawal[]? Withdrawals { get; set; }

    public ulong BlobGasUsed { get; set; }

    public ulong ExcessBlobGas { get; set; }
}
