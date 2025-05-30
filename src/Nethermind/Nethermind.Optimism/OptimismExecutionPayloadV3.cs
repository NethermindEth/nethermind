// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism;

/// <summary>
/// The Optimism specific execution payload.
/// </summary>
// TODO: Separate `OptimismExecutionPayloadV3` from a new `OptimismExecutionPayloadV4`
// The former is equivalent to `ExecutionPayloadV3`, while the latter contains an additional `WithdrawalsRoot` field.
// This is required to support pre and post Isthmus
public class OptimismExecutionPayloadV3 : ExecutionPayloadV3
{
    public Hash256? WithdrawalsRoot { get; set; }

    protected override Hash256? BuildWithdrawalsRoot() => WithdrawalsRoot ?? Keccak.EmptyTreeHash;
}
