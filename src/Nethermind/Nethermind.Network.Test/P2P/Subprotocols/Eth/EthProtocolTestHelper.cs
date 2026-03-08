// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

internal static class EthProtocolTestHelper
{
    public static void HandleZeroMessage<T>(IMessageSerializationService svc, IZeroProtocolHandler handler, T msg, int messageCode)
        where T : MessageBase
    {
        IByteBuffer packet = svc.ZeroSerialize(msg);
        packet.ReadByte();
        handler.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)messageCode });
    }

    public static void HandleIncomingStatusMessage(IMessageSerializationService svc, IZeroProtocolHandler handler, Block genesisBlock)
    {
        using var statusMsg = new StatusMessage { GenesisHash = genesisBlock.Hash, BestHash = genesisBlock.Hash };

        IByteBuffer statusPacket = svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    public static void HandleIncomingStatusMessage69(IMessageSerializationService svc, IZeroProtocolHandler handler, Block genesisBlock, byte protocolVersion)
    {
        using var statusMsg = new StatusMessage69 { ProtocolVersion = protocolVersion, GenesisHash = genesisBlock.Hash!, LatestBlockHash = genesisBlock.Hash! };

        IByteBuffer statusPacket = svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }
}
