// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <summary>Whether a walk to a value keeps the path of the node it has reached.</summary>
    /// <remarks>Path-keyed node storage finds a node by its path, so the walk has to track it. A property rather
    /// than a const, which would make every guarded statement unreachable code (CS0162) in the guest build.</remarks>
    private static bool TracksPath => true;

    /// <summary>How <see cref="ShouldUpdateChild"/> is inlined: left to the JIT.</summary>
    private const MethodImplOptions ShouldUpdateChildInlining = default;
}
