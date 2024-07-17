// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

/// Note, a tree based kademlia will likely change this significantly.
public interface IDistanceCalculator<THash>
{
    int CalculateDistance(THash h1, THash h2);
    int MaxDistance { get; }
    THash RandomizeHashAtDistance(THash hash, int distance);

    /// Compare distance of a and b againts target
    int Compare(THash a, THash b, THash target);
}
