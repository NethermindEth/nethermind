// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class NodeEventArgs : EventArgs
    {
        public Node Node { get; }

        public NodeEventArgs(Node node)
        {
            Node = node;
        }
    }
}
