// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.StateComposition.Visitors;

/// <summary>Inline storage for 16 longs — no heap allocation on the enclosing type.</summary>
[InlineArray(Length)]
internal struct Long16
{
    public const int Length = 16;
    private long _element;
}
