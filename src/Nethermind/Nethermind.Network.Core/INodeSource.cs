// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Network;

public interface INodeSource
{
    IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken);

    event EventHandler<NodeEventArgs> NodeRemoved;
}
