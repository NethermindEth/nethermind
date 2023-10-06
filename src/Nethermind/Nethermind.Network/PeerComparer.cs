// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Stats;

namespace Nethermind.Network
{
    internal class PeerComparer : IComparer<Peer>
    {
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

            int reputation = -x.Node.CurrentReputation.CompareTo(y.Node.CurrentReputation);
            return reputation;
        }
    }
}
