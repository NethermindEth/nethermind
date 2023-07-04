// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery;

public interface IDiscoveryMsgListener
{
    void OnIncomingMsg(DiscoveryMsg msg);
}
