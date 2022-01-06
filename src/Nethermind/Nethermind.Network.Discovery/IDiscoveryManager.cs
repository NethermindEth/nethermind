//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public interface IDiscoveryManager : IDiscoveryMsgListener
{
    IMsgSender MsgSender { set; }
    INodeLifecycleManager? GetNodeLifecycleManager(Node node, bool isPersisted = false);
    void SendMessage(DiscoveryMsg discoveryMsg);
    Task<bool> WasMessageReceived(Keccak senderIdHash, MsgType msgType, int timeout);
    event EventHandler<NodeEventArgs> NodeDiscovered;

    IReadOnlyCollection<INodeLifecycleManager> GetNodeLifecycleManagers();
    IReadOnlyCollection<INodeLifecycleManager> GetOrAddNodeLifecycleManagers(Func<INodeLifecycleManager, bool> query);
}
