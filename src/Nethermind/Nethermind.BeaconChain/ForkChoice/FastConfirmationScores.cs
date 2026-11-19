// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Maintains latest-message scores for a fixed snapshot of validator balances and slashing flags.</summary>
/// <remarks>
/// Initialization visits all validators. Subsequent calculations visit changed voters, equivocators and retained blocks.
/// Direct support includes unknown roots so inserting or pruning blocks does not require a validator rescan.
/// </remarks>
internal sealed class FastConfirmationScores(ulong[] balances, bool[] slashed)
{
    private readonly Hash256?[] _countedRoots = new Hash256?[balances.Length];
    private readonly Dictionary<Hash256, ulong> _directScores = [];
    private readonly HashSet<int> _dirty = [];
    private readonly HashSet<ulong> _equivocators = [];
    private bool _initialized;

    internal void MarkDirty(int validatorIndex)
    {
        if ((uint)validatorIndex < (uint)balances.Length) _dirty.Add(validatorIndex);
    }

    internal ulong[] Calculate(ProtoArrayForkChoice tree, IReadOnlySet<ulong> equivocators)
    {
        if (!_initialized)
        {
            for (int i = 0; i < balances.Length; i++) Update(i);
            _initialized = true;
        }
        else
        {
            foreach (ulong index in _equivocators)
                if (!equivocators.Contains(index) && index < (ulong)balances.Length) MarkDirty((int)index);
            foreach (ulong index in equivocators)
                if (!_equivocators.Contains(index) && index < (ulong)balances.Length) MarkDirty((int)index);
            foreach (int index in _dirty) Update(index);
        }
        _dirty.Clear();
        _equivocators.Clear();
        _equivocators.UnionWith(equivocators);

        ulong[] scores = new ulong[tree.Nodes.Count];
        for (int i = scores.Length - 1; i >= 0; i--)
        {
            ProtoNode node = tree.Nodes[i];
            if (_directScores.TryGetValue(node.Root, out ulong direct)) scores[i] += direct;
            if (node.Parent is int parent) scores[parent] += scores[i];
        }
        return scores;

        void Update(int index)
        {
            ulong balance = balances[index];
            Hash256? root = !slashed[index] && balance != 0 && !equivocators.Contains((ulong)index)
                ? tree.LatestVote((ulong)index)?.NextRoot
                : null;
            Hash256? previousRoot = _countedRoots[index];
            if (root == previousRoot) return;
            if (previousRoot is not null)
            {
                ulong remaining = _directScores[previousRoot] - balance;
                if (remaining == 0) _directScores.Remove(previousRoot);
                else _directScores[previousRoot] = remaining;
            }
            if (root is not null)
            {
                _directScores.TryGetValue(root, out ulong previous);
                _directScores[root] = previous + balance;
            }
            _countedRoots[index] = root;
        }
    }
}
