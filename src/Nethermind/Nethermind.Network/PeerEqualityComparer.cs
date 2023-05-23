// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Network
{
    internal class PeerEqualityComparer : IEqualityComparer<Peer>
    {
        public bool Equals(Peer x, Peer y)
        {
            if (x is null || y is null)
            {
                return false;
            }

            return x.Node.Id.Equals(y.Node.Id);
        }

        public int GetHashCode(Peer obj) => obj?.Node is null ? 0 : obj.Node.GetHashCode();
    }
}
