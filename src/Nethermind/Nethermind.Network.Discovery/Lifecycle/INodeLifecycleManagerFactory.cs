// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Lifecycle;

public interface INodeLifecycleManagerFactory
{
    INodeLifecycleManager CreateNodeLifecycleManager(Node node);
    IDiscoveryManager DiscoveryManager { set; }
}
