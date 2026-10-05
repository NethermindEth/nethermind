// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <inheritdoc cref="TracksPath" path="/summary"/>
    /// <remarks>
    /// The guest's nodes come from the witness, keyed by hash alone (see <c>WitnessNodeStorage.zkevm.cs</c>), so
    /// the path would only label exceptions, at a nibble write and a nibble clear per level.
    /// </remarks>
    private static bool TracksPath => false;

    /// <summary>How <see cref="ShouldUpdateChild"/> is inlined: always.</summary>
    /// <remarks>Asked per level as a trie write climbs back to the root; out of line, the call costs more than its few compares.</remarks>
    private const MethodImplOptions ShouldUpdateChildInlining = MethodImplOptions.AggressiveInlining;
}
