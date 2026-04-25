// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.StateComposition.Visitors;

/// <summary>Inline storage for 16 DepthCounter rows — keeps the per-thread visitor allocation-free.</summary>
[InlineArray(Long16.Length)]
internal struct DepthCounter16
{
    private DepthCounter _element;
}
