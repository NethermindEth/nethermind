// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Discovered candidates waiting to be dialed, taken by how many wanted columns each custodies, oldest first on a tie.</summary>
/// <remarks>With no wanted column every candidate ranks equal, so candidates are taken in arrival order.</remarks>
internal sealed class CustodyRankedCandidates(int capacity)
{
    private readonly List<BeaconPeerCandidate> _candidates = [];

    public int Count => _candidates.Count;

    /// <summary>Adds a candidate, replacing a waiting one with the same peer id; past capacity the oldest of the lowest ranked is dropped.</summary>
    public void Add(BeaconPeerCandidate candidate, IReadOnlyList<ulong> wanted)
    {
        int existing = _candidates.FindIndex(c => string.Equals(c.PeerId, candidate.PeerId, StringComparison.Ordinal));
        if (existing >= 0)
        {
            _candidates[existing] = candidate;
            return;
        }

        _candidates.Add(candidate);
        if (_candidates.Count > capacity)
        {
            _candidates.RemoveAt(IndexOfRank(wanted, best: false));
        }
    }

    public bool TryTake(IReadOnlyList<ulong> wanted, [NotNullWhen(true)] out BeaconPeerCandidate? candidate)
    {
        if (_candidates.Count == 0)
        {
            candidate = null;
            return false;
        }

        int index = IndexOfRank(wanted, best: true);
        candidate = _candidates[index];
        _candidates.RemoveAt(index);
        return true;
    }

    private int IndexOfRank(IReadOnlyList<ulong> wanted, bool best)
    {
        if (wanted.Count == 0)
        {
            return 0;
        }

        int chosen = 0;
        int chosenCovered = _candidates[0].Custody.CountCustodied(wanted);
        for (int i = 1; i < _candidates.Count; i++)
        {
            int covered = _candidates[i].Custody.CountCustodied(wanted);
            if (best ? covered > chosenCovered : covered < chosenCovered)
            {
                chosen = i;
                chosenCovered = covered;
            }
        }

        return chosen;
    }
}
