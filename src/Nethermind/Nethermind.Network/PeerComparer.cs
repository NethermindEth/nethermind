// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Network
{
    internal readonly struct PeerComparer : IComparer<Peer>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(Peer? x, Peer? y)
        {
            if (x is null) return y is null ? 0 : 1;
            if (y is null) return -1;

            int staticCompare = y.Node.IsStatic.CompareTo(x.Node.IsStatic);
            return staticCompare != 0
                ? staticCompare
                : y.Node.CurrentReputation.CompareTo(x.Node.CurrentReputation);
        }
    }
}
