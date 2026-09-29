// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>The block-wide index the next log that takes effect receives; callTracer log indexes continue from it.</summary>
/// <remarks>A replay that traces every transaction of a block shares one instance, so each transaction continues
/// from the logs the preceding ones committed, whether or not the body was ever stored. A trace of a single stored
/// transaction gets its own instance seeded from the block's receipts, and a synthetic call starts from zero.</remarks>
/// <param name="next">The index of the first log the traced transaction may emit.</param>
public sealed class BlockLogIndex(int next = 0)
{
    /// <summary>The index the next committed log receives; the tracer moves it past each transaction's receipt logs
    /// when the transaction ends.</summary>
    public int Next { get; set; } = next;
}
