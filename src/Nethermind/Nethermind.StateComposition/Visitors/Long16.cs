// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.StateComposition.Visitors;

/// <summary>
/// Fixed-size inline buffer of 16 <see cref="long"/> values. Embeds the storage
/// directly in the enclosing type, avoiding heap allocation, GC tracking, and
/// the indirection of a managed array. Indexer and slice operations come from
/// the compiler-synthesized <see cref="InlineArrayAttribute"/> support.
/// </summary>
[InlineArray(Length)]
internal struct Long16
{
    public const int Length = 16;
    private long _element;
}
