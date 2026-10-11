// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;

namespace Nethermind.Synchronization.Test.Mocks;

internal static class HistoryPeerTestHelpers
{
    internal static PeerInfo Create(PublicKey key, ulong earliest, bool supportsAccessLists = false)
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Node(key, "127.0.0.1", 30303));
        peer.EarliestBlock.Returns(earliest);
        peer.IsInitialized.Returns(true);
        peer.ProtocolVersion.Returns(supportsAccessLists ? (byte)71 : (byte)69);
        return new PeerInfo(peer);
    }
}
