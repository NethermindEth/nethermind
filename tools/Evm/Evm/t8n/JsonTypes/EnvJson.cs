// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Evm.t8n.JsonTypes;

public class EnvJson
{
    public Address? CurrentCoinbase { get; set; }
    public long CurrentGasLimit { get; set; }
    public long CurrentNumber { get; set; }
    public ulong CurrentTimestamp { get; set; }
    public Withdrawal[] Withdrawals { get; set; } = [];

    public UInt256? CurrentRandom { get; set; }
    public ulong ParentTimestamp { get; set; }
    public UInt256? ParentDifficulty { get; set; }
    public UInt256? CurrentBaseFee { get; set; }
    public UInt256? CurrentDifficulty { get; set; }
    public Hash256? ParentUncleHash { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
    public UInt256? ParentBaseFee { get; set; }
    public long ParentGasUsed { get; set; }
    public long ParentGasLimit { get; set; }
    public ulong? ParentExcessBlobGas { get; set; }
    public ulong? CurrentExcessBlobGas { get; set; }
    public ulong? ParentBlobGasUsed { get; set; }

    public Dictionary<string, Hash256> BlockHashes { get; set; } = [];
    public Ommer[] Ommers { get; set; } = [];

    public Hash256? GetCurrentRandomHash256()
    {
        if (CurrentRandom == null) return null;

        Span<byte> bytes = stackalloc byte[32];
        CurrentRandom?.ToBigEndian(bytes);
        return new Hash256(bytes);
    }
}
