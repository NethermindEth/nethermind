// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>
/// Thrown when an EIP-8037 state-gas accounting invariant is violated. Such a violation is only reachable through an
/// internal accounting bug, never through transaction input.
/// </summary>
/// <remarks>
/// Caught at the transaction-processing boundary and converted into a failed <see cref="TransactionResult"/> so the
/// failure takes a shape block processing already understands: block production skips the offending transaction and
/// block import rejects the block as <c>INVALID</c>, rather than the exception escaping as a node fault.
/// </remarks>
internal sealed class StateGasInvariantViolatedException(string message) : Exception(message);
