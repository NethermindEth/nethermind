// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
