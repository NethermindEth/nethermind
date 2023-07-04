// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Result of call.
/// 
/// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#transitionconfigurationv1"/>
/// </summary>
public class TransitionConfigurationV1
{
    /// <summary>
    /// Maps on the TERMINAL_TOTAL_DIFFICULTY parameter of EIP-3675
    /// </summary>
    public UInt256? TerminalTotalDifficulty { get; set; }

    /// <summary>
    /// Maps on TERMINAL_BLOCK_HASH parameter of EIP-3675
    /// </summary>
    public Keccak TerminalBlockHash { get; set; } = Keccak.Zero;

    /// <summary>
    /// Maps on TERMINAL_BLOCK_NUMBER parameter of EIP-3675
    /// </summary>
    public long TerminalBlockNumber { get; set; }
}
