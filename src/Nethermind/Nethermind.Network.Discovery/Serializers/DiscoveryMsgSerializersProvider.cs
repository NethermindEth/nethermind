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

using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;

namespace Nethermind.Network.Discovery.Serializers;

public class DiscoveryMsgSerializersProvider : IDiscoveryMsgSerializersProvider
{
    private readonly IMessageSerializationService _msgSerializationService;
    private readonly PingMsgSerializer _pingMsgSerializer;
    private readonly PongMsgSerializer _pongMsgSerializer;
    private readonly FindNodeMsgSerializer _findNodeMsgSerializer;
    private readonly NeighborsMsgSerializer _neighborsMsgSerializer;

    public DiscoveryMsgSerializersProvider(IMessageSerializationService msgSerializationService,
        IEcdsa ecdsa,
        IPrivateKeyGenerator privateKeyGenerator,
        INodeIdResolver nodeIdResolver)
    {
        PingMsgSerializer pingSerializer = new(ecdsa, privateKeyGenerator, nodeIdResolver);
        PongMsgSerializer pongSerializer = new(ecdsa, privateKeyGenerator, nodeIdResolver);
        FindNodeMsgSerializer findNodeSerializer = new(ecdsa, privateKeyGenerator, nodeIdResolver);
        NeighborsMsgSerializer neighborsSerializer = new(ecdsa, privateKeyGenerator, nodeIdResolver);

        _msgSerializationService = msgSerializationService;
        _pingMsgSerializer = pingSerializer;
        _pongMsgSerializer = pongSerializer;
        _findNodeMsgSerializer = findNodeSerializer;
        _neighborsMsgSerializer = neighborsSerializer;
    }

    public void RegisterDiscoverySerializers()
    {
        _msgSerializationService.Register(_pingMsgSerializer);
        _msgSerializationService.Register(_pongMsgSerializer);
        _msgSerializationService.Register(_findNodeMsgSerializer);
        _msgSerializationService.Register(_neighborsMsgSerializer);
    }
}
