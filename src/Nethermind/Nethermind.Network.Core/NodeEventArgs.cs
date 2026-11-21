// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class NodeEventArgs(Node node) : EventArgs
    {
        public Node Node { get; } = node;
    }

    /// <summary>
    /// Raised when a node is explicitly removed by the operator (e.g. via admin_removePeer).
    /// Receivers must disconnect any active P2P session unconditionally.
    /// </summary>
    public class ExplicitNodeRemovalEventArgs(Node node) : NodeEventArgs(node);
}
