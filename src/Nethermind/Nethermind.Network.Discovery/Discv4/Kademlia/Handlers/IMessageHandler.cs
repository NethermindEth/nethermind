// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Discv4.Messages;

namespace Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;

internal interface IMessageHandler
{
    bool Handle(DiscoveryMsg msg);
}
