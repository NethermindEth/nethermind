// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>
/// A block tracer that can forget the transaction it traced last, for a block producer that executes a
/// transaction and then leaves it out of the block.
/// </summary>
public interface ITxDiscardAwareTracer
{
    /// <summary>Drops everything recorded for the most recently traced transaction.</summary>
    void DiscardLastTx();
}
