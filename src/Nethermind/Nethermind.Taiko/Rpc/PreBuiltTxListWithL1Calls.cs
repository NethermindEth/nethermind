// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Taiko.Rpc;

/// <summary>
/// Represents the result of transaction pool content with L1SLOAD calls tracing.
/// Contains both the transaction lists and the L1SLOAD calls detected during execution.
/// </summary>
public sealed class PreBuiltTxListWithL1Calls(PreBuiltTxList[] txLists, L1SloadCall[] l1SloadCalls)
{
    /// <summary>
    /// The transaction lists that can be included in a block
    /// </summary>
    public PreBuiltTxList[] TxLists { get; } = txLists;

    /// <summary>
    /// The L1SLOAD calls detected during transaction execution
    /// </summary>
    public L1SloadCall[] L1SloadCalls { get; } = l1SloadCalls;
}
