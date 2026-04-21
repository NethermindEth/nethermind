// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Taiko;

public class TaikoExecutionPayloadV4 : ExecutionPayloadV4
{
    public Hash256 WithdrawalsHash { get; set; } = Keccak.Zero;
    public Hash256 TransactionsHash { get; set; } = Keccak.Zero;

    protected override int GetExecutionPayloadVersion() => this switch
    {
        { BlockAccessList: not null } => 4,
        { BlobGasUsed: not null } or { ExcessBlobGas: not null } or { ParentBeaconBlockRoot: not null } => 3,
        { WithdrawalsHash: not null } or { Withdrawals: not null } => 2, // modified
        _ => 1
    };
}
