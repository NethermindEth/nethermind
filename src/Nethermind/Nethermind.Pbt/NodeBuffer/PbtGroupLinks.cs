// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>The first physical descendant group of each boundary slot, possibly across compressed absent groups.</summary>
[InlineArray(PbtThreeLevelGroupGeometry.BoundarySlots)]
public struct PbtGroupLinks
{
    private PbtGroupReference _element0;

    /// <summary>Links for a group loaded without traversal metadata, which must resolve its descendants from the snapshot.</summary>
    public static PbtGroupLinks Unknown
    {
        get
        {
            PbtGroupLinks links = default;
            for (int slot = 0; slot < PbtThreeLevelGroupGeometry.BoundarySlots; slot++) links[slot] = PbtGroupReference.Unknown;
            return links;
        }
    }
}
