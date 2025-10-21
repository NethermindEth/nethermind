// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Network
{
    internal class PeerComparer : IComparer<Peer>
    {
        private readonly bool _useDiversityScoring;

        public PeerComparer(bool useDiversityScoring = false)
        {
            _useDiversityScoring = useDiversityScoring;
        }

        public int Compare(Peer x, Peer y)
        {
            if (x is null)
            {
                return y is null ? 0 : 1;
            }

            if (y is null)
            {
                return -1;
            }

            int staticValue = -x.Node.IsStatic.CompareTo(y.Node.IsStatic);
            if (staticValue != 0)
            {
                return staticValue;
            }

            // If diversity scoring is enabled, use diversity score
            // Otherwise fall back to reputation-based sorting
            if (_useDiversityScoring)
            {
                int diversityScore = -x.DiversityScore.CompareTo(y.DiversityScore);
                if (diversityScore != 0)
                {
                    return diversityScore;
                }
            }

            int reputation = -x.Node.CurrentReputation.CompareTo(y.Node.CurrentReputation);
            return reputation;
        }
    }
}
