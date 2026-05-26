// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Taiko;

public class TaikoExecutionPayloadV3 : ExecutionPayloadV3
{
    public Hash256 WithdrawalsHash { get; set; } = Keccak.Zero;
    public Hash256 TransactionsHash { get; set; } = Keccak.Zero;

    /// <summary>
    /// Unzen sidecar field: carries the header difficulty (ZK gas used) through the Engine API.
    /// </summary>
    public UInt256? HeaderDifficulty { get; set; }

    protected override int GetExecutionPayloadVersion() => this switch
    {
        { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
        { WithdrawalsHash: not null } or { Withdrawals: not null } => 2, // modified
        _ => 1
    };

    public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
    {
        Result<Block> result = base.TryGetBlock(totalDifficulty);
        if (result.IsSuccess && HeaderDifficulty is not null)
        {
            result.Data.Header.Difficulty = HeaderDifficulty.Value;
        }
        return result;
    }
}
