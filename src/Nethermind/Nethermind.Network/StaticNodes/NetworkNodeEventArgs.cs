// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;

namespace Nethermind.Network.StaticNodes
{
    public class NetworkNodeEventArgs(NetworkNode node) : EventArgs
    {
        public NetworkNode Node { get; } = node;
    }
}
