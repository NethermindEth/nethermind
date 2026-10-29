// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

/// <summary>The sorted runs a <see cref="LoserTree{TRuns}"/> merges, each read through its current head.</summary>
public interface ILoserTreeRuns
{
    /// <summary>The number of runs.</summary>
    int Count { get; }

    /// <summary>Whether <paramref name="run"/> has no head left.</summary>
    bool IsExhausted(int run);

    /// <summary>Orders the heads of two runs, neither of them exhausted.</summary>
    int CompareHeads(int left, int right);

    /// <summary>Moves <paramref name="run"/> to its next head.</summary>
    void Advance(int run);
}

/// <summary>Tournament tree that yields the heads of k sorted runs in ascending order.</summary>
/// <remarks>
/// Each internal node holds the loser of a match and <c>_tree[0]</c> the overall winner, so <see cref="Pop"/> replays
/// only the winner's leaf-to-root path: O(log k) comparisons per head rather than O(k). Equal heads come out in run
/// order, which keeps the merge stable. Leaf <c>k</c> is a sentinel that seeds the tree.
/// </remarks>
public ref struct LoserTree<TRuns> where TRuns : ILoserTreeRuns, allows ref struct
{
    private TRuns _runs;
    private readonly Span<int> _tree;
    private readonly int _k;

    /// <param name="runs">The runs, each sorted by <see cref="ILoserTreeRuns.CompareHeads"/>.</param>
    /// <param name="tree">Scratch for the tree, at least <see cref="ILoserTreeRuns.Count"/> long.</param>
    public LoserTree(TRuns runs, Span<int> tree)
    {
        _runs = runs;
        _k = runs.Count;
        _tree = tree[.._k];
        _tree.Fill(_k);
        for (int run = _k - 1; run >= 0; run--) Replay(run);
    }

    /// <summary>Gets the run holding the smallest head, or false once every run is exhausted.</summary>
    public readonly bool TryPeek(out int run)
    {
        run = _k == 0 ? _k : _tree[0];
        return run != _k && !_runs.IsExhausted(run);
    }

    /// <summary>Advances the run <see cref="TryPeek"/> returned.</summary>
    public void Pop()
    {
        int winner = _tree[0];
        _runs.Advance(winner);
        Replay(winner);
    }

    private void Replay(int run)
    {
        for (int parent = (run + _k) >> 1; parent > 0; parent >>= 1)
        {
            if (Compare(run, _tree[parent]) > 0)
            {
                (run, _tree[parent]) = (_tree[parent], run);
            }
        }
        _tree[0] = run;
    }

    // The sentinel orders before every run, and an exhausted run after every run with a head left.
    private readonly int Compare(int left, int right)
    {
        if (left == _k) return right == _k ? 0 : -1;
        if (right == _k) return 1;

        bool leftExhausted = _runs.IsExhausted(left);
        bool rightExhausted = _runs.IsExhausted(right);
        if (leftExhausted || rightExhausted)
        {
            return leftExhausted == rightExhausted ? left.CompareTo(right) : leftExhausted ? 1 : -1;
        }

        int cmp = _runs.CompareHeads(left, right);
        return cmp != 0 ? cmp : left.CompareTo(right);
    }
}
