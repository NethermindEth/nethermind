// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;

/// <summary>
/// Output of the <c>stateGasTracer</c> (execution-apis <c>StateGasTrace</c> schema). Satisfies
/// <c>regularGasUsed + stateGasUsed == gasUsed + gasRefund</c> except when the EIP-7623 calldata floor binds.
/// </summary>
[JsonConverter(typeof(StateGasTraceConverter))]
public class StateGasTrace
{
    /// <summary>Gas used by the transaction as reported in its receipt (post-refund, post-floor).</summary>
    public ulong GasUsed { get; init; }

    /// <summary>Gross (pre-refund) regular gas used - the transaction's contribution to <c>block_regular_gas_used</c> (EIP-7778).</summary>
    public ulong RegularGasUsed { get; init; }

    /// <summary>Gross (pre-refund) state gas used - the transaction's contribution to <c>block_state_gas_used</c> (EIP-8037).</summary>
    public ulong StateGasUsed { get; init; }

    /// <summary>EIP-3529 gas refund applied to the transaction (capped at one fifth of the pre-refund gas).</summary>
    public ulong GasRefund { get; init; }
}
