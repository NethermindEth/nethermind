// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization
{
    public class AllocationChangeEventArgs
    {
        public AllocationChangeEventArgs(PeerInfo? previous, PeerInfo? current)
        {
            Previous = previous;
            Current = current;
        }

        public PeerInfo? Previous { get; }

        public PeerInfo? Current { get; }
    }
}
