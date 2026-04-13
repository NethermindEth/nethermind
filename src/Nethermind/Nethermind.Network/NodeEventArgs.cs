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
}
