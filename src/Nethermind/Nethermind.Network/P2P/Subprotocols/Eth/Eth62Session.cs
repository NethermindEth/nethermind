/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public class Eth62Session : SessionBase, ISession
    {
        public Eth62Session(
            IMessageSerializationService serializer,
            IPacketSender packetSender,
            ILogger logger,
            PublicKey remoteNodeId,
            int remotePort)
            : base(serializer, packetSender, remoteNodeId, logger)
        {
            RemotePort = remotePort;
        }

        public string ProtocolCode { get; } = "eth";

        public int MessageIdSpaceSize { get; } = 7;

        public void HandleMessage(Packet packet)
        {
            switch (packet.PacketType)
            {
                case Eth62MessageCode.Status:
                    Deserialize<StatusMessage>(packet.Data);
                    Logger.Log($"ETH received status");
                    break;
                case Eth62MessageCode.NewBlockHashes:
                    Deserialize<NewBlockHashesMessage>(packet.Data);
                    Logger.Log($"ETH received new block hashes");
                    break;
            }
        }

        public void Init()
        {
            Logger.Log($"ETH subprotocol initializing");
            StatusMessage statusMessage = new StatusMessage();
            statusMessage.NetworkId = 1;
            statusMessage.ProtocolVersion = 62;
            statusMessage.TotalDifficulty = 131200;
            statusMessage.BestHash = Keccak.Zero;
            statusMessage.GenesisHash = Keccak.Zero;
            //

            Logger.Log($"ETH sending status");
            Send(statusMessage);
        }

        public void Close()
        {
        }
    }
}