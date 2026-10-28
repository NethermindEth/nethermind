// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>The pointer of the first physical descendant group of each boundary slot, possibly across compressed absent groups, or 0 when it is not in the buffer.</summary>
[InlineArray(PbtTwoLevelGroupGeometry.BoundarySlots)]
public struct PbtGroupLinks
{
    private ulong _element0;
}
