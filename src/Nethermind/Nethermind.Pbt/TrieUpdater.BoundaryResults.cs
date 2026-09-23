// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The boundary slots holding a node anchored at a group other than the traversal cursor.</summary>
    /// <remarks>
    /// A folded result is anchored at its own branch depth, and a decomposed ancestor view keeps the group it was
    /// read from, so neither can be interpreted against the cursor the way a <see cref="Frontier"/> entry is. Each
    /// slot therefore keeps its own group anchor, restored into a caller-owned buffer when the slot is taken.
    /// </remarks>
    internal struct BoundaryResults
    {
        private ResultBuffer _results;
        private ushort _mask;

        internal readonly bool Holds(int slot) => (_mask & (1 << slot)) != 0;

        internal TraversalSubtree Take(int slot, Span<byte> scratch)
        {
            _mask &= (ushort)~(1 << slot);
            OwnedSubtree result = _results[slot];
            _results[slot] = default;
            return result.Borrow(scratch);
        }

        internal void Set(int slot, ref TraversalSubtree result)
        {
            _mask |= (ushort)(1 << slot);
            _results[slot] = new(result.GroupPath.ToPath<TPath>(), Subtree.Move(ref result.Node));
        }

        internal void Clear(int slot)
        {
            _mask &= (ushort)~(1 << slot);
            _results[slot] = default;
        }
    }

    [InlineArray(PbtFourLevelGroupGeometry.BoundarySlots)]
    private struct ResultBuffer { private OwnedSubtree _element; }
}
