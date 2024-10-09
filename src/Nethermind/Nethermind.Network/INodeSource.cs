// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public interface INodeSource
{
    IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken);
    event EventHandler<NodeEventArgs> NodeRemoved;
}
