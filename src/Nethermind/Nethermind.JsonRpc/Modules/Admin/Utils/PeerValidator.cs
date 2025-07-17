// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin.Utils
{
    public static class PeerValidator
    {
        public static void ValidatePeer(Peer peer)
        {
            if (peer is null)
            {
                throw new ArgumentNullException(nameof(peer));
            }

            if (peer.Node is null)
            {
                throw new ArgumentException("Peer must have a valid node", nameof(peer));
            }
        }
    }
}
