// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Eez.Network;

internal sealed class SequencerPeerFactoryBuilder(IServiceProvider services)
    : PeerFactoryBuilderBase<SequencerPeerFactoryBuilder, PeerFactory>(services)
{
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();
        Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>()], [Get<MultistreamProtocol>()],
            [Get<YamuxProtocol>()], [Get<SequencerGossipSelector>()],
            [Get<GossipsubProtocolV12>(), Get<GossipsubProtocolV11>(), Get<GossipsubProtocol>(), .. additionalProtocols]);
        return [tcp];
    }
}

internal sealed class SequencerGossipSelector(MultistreamProtocol inner, SequencerHeadCodec codec) : IConnectionProtocol
{
    public string Id => inner.Id;

    public Task ListenAsync(IChannel channel, IConnectionContext context) =>
        inner.ListenAsync(new SequencerFrameChannel(channel, codec.Topic), context);

    public Task DialAsync(IChannel channel, IConnectionContext context) =>
        inner.DialAsync(new SequencerFrameChannel(channel, codec.Topic), context);
}
