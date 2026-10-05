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
}
