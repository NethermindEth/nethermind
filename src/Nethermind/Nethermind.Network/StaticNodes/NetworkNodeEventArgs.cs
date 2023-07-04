// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;

namespace Nethermind.Network.StaticNodes
{
    public class NetworkNodeEventArgs : EventArgs
    {
        public NetworkNode Node { get; }

        public NetworkNodeEventArgs(NetworkNode node)
        {
            Node = node;
        }
    }
}
