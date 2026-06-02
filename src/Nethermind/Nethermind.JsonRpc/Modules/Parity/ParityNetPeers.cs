// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityNetPeers
    {
        public int Active { get; set; }

        public int Connected { get; set; }

        public int Max { get; set; }

        public PeerInfo[] Peers { get; set; }

        public ParityNetPeers()
        {
        }
    }
}
