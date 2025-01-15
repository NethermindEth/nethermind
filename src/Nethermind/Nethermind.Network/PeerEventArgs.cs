// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network;

public class PeerEventArgs : EventArgs
{
    public PeerEventArgs(Peer peer)
    {
        Peer = peer;
    }

    public Peer Peer { get; set; }
}
