//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
