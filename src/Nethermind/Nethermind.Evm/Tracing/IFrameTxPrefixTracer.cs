// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Optional capability for a tracer enforcing the EIP-8141 validation-prefix rules: the transaction
/// processor announces each prefix frame before it executes.
/// </summary>
/// <remarks>The deploy-frame carve-outs are scoped to one frame, and a tracer sees only opcodes, so it
/// cannot tell the frames apart on its own. Announcing entry rather than tracking call depth also gives
/// the carve-outs the frame's whole call subtree, which is where a factory does its work.</remarks>
public interface IFrameTxPrefixTracer
{
    /// <param name="isDeployFrame">Whether the frame about to run is the prefix-opening <c>deploy</c> frame.</param>
    /// <param name="target">The address the frame dispatches, already resolved from <c>frame.Target</c>.</param>
    void StartPrefixFrame(bool isDeployFrame, Address target);

    /// <summary>The VERIFY gas the prefix may spend, signature verification included.</summary>
    /// <remarks>EIP-8141's <c>MAX_VERIFY_GAS</c> for mempool admission; EIP-8369 Profile 2 replay allows up to
    /// <c>MAX_VERIFY_GAS_PER_TX</c>.</remarks>
    ulong MaxVerifyGas => Eip8141Constants.MaxVerifyGas;

    /// <summary>The <c>current_slot</c> EIP-8272 recent-root references are checked against, or <c>null</c> for the
    /// slot after the executing header, which is where mempool admission expects the transaction to land.</summary>
    ulong? RecentRootAnchorSlot => null;
}
