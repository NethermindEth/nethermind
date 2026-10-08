// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Trie;

public partial struct TreePath
{
    /// <summary>Adds a level to <see cref="Length"/> without writing its nibble, which stays zero.</summary>
    /// <remarks>For walks that need the depth alone; the path stays canonical, zero past and within the added level.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendDepth() => Length++;

    /// <summary>Removes a level <see cref="AppendDepth"/> added.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void TruncateDepth() => Length--;
}
