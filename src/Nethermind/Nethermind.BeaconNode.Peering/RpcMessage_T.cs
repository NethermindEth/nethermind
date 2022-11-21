// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.P2p;

namespace Nethermind.BeaconNode.Peering
{
    public class RpcMessage<T>
    {
        public RpcMessage(string peerId, RpcDirection direction, T content)
        {
            PeerId = peerId;
            Direction = direction;
            Content = content;
        }

        public T Content { get; }
        public RpcDirection Direction { get; }
        public string PeerId { get; }
    }
}
