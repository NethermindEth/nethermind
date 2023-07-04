// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public interface INodeSource
{
    List<Node> LoadInitialList();
    event EventHandler<NodeEventArgs> NodeAdded;
    event EventHandler<NodeEventArgs> NodeRemoved;
}
