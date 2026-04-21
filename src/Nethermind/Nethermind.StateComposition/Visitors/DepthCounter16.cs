// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.StateComposition.Visitors;

/// <summary>
/// Fixed-size inline buffer of 16 <see cref="DepthCounter"/> values (one slot
/// per tracked trie depth). Embedding the counters inline keeps per-thread
/// <see cref="VisitorCounters"/> allocation-free for the hot per-node path and
/// improves cache locality — all depth rows sit contiguously with the enclosing
/// struct instead of hanging off a separate heap array.
/// </summary>
[InlineArray(Long16.Length)]
internal struct DepthCounter16
{
    private DepthCounter _element;
}
