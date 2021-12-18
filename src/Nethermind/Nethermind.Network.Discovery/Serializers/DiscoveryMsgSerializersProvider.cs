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
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery.Serializers;

public class DiscoveryMsgSerializersProvider : IDiscoveryMsgSerializersProvider
{
    private readonly IMessageSerializationService _msgSerializationService;
    private readonly PingMsgSerializer _pingMsgSerializer;
    private readonly PongMsgSerializer _pongMsgSerializer;
    private readonly FindNodeMsgSerializer _findNodeMsgSerializer;
    private readonly NeighborsMsgSerializer _neighborsMsgSerializer;
    private readonly EnrRequestMsgSerializer _enrRequestMsgSerializer;
    private readonly EnrResponseMsgSerializer _enrResponseMsgSerializer;

    public DiscoveryMsgSerializersProvider(IMessageSerializationService msgSerializationService,
        IEcdsa ecdsa,
        IPrivateKeyGenerator privateKeyGenerator,
        INodeIdResolver nodeIdResolver)
    {
        _msgSerializationService = msgSerializationService;
        _pingMsgSerializer = new PingMsgSerializer(ecdsa, privateKeyGenerator, nodeIdResolver);
        _pongMsgSerializer = new PongMsgSerializer(ecdsa, privateKeyGenerator, nodeIdResolver);
        _findNodeMsgSerializer = new FindNodeMsgSerializer(ecdsa, privateKeyGenerator, nodeIdResolver);
        _neighborsMsgSerializer = new NeighborsMsgSerializer(ecdsa, privateKeyGenerator, nodeIdResolver);
        _enrRequestMsgSerializer = new EnrRequestMsgSerializer(ecdsa, privateKeyGenerator, nodeIdResolver);
        _enrResponseMsgSerializer = new EnrResponseMsgSerializer(ecdsa, privateKeyGenerator, nodeIdResolver);
    }

    public void RegisterDiscoverySerializers()
    {
        _msgSerializationService.Register(_pingMsgSerializer);
        _msgSerializationService.Register(_pongMsgSerializer);
        _msgSerializationService.Register(_findNodeMsgSerializer);
        _msgSerializationService.Register(_neighborsMsgSerializer);
        _msgSerializationService.Register(_enrRequestMsgSerializer);
        _msgSerializationService.Register(_enrResponseMsgSerializer);
    }
}
