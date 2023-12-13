// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery;

public interface IMsgSender
{
    Task SendMsg(DiscoveryMsg discoveryMsg);
}
