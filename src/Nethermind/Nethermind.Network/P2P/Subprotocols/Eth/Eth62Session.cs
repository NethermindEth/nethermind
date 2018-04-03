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

using System;
using System.Diagnostics;
using System.Numerics;
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

        private bool _statusSent;
        
        public virtual int ProtocolVersion => 62;
        
        public string ProtocolCode => "eth";

        public virtual int MessageIdSpaceSize => 7;

        public virtual void HandleMessage(Packet packet)
        {
            switch (packet.PacketType)
            {
                case Eth62MessageCode.Status:
                    // session established here
                    StatusMessage message = Deserialize<StatusMessage>(packet.Data);
                    Logger.Log("ETH received status with" +
                               Environment.NewLine + $" prot version\t{message.ProtocolVersion}" +
                               Environment.NewLine + $" network ID\t{message.NetworkId}," +
                               Environment.NewLine + $" genesis hash\t{message.GenesisHash}," + 
                               Environment.NewLine + $" best hash\t{message.BestHash}," +
                               Environment.NewLine + $" difficulty\t{message.TotalDifficulty}");
                    
                    Debug.Assert(_statusSent, "Expecting Init() to have been called by this point");
                    SessionEstablished?.Invoke(this, EventArgs.Empty);
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
            statusMessage.NetworkId = NewerRopsten.ChainId;
            statusMessage.ProtocolVersion = ProtocolVersion;
            statusMessage.TotalDifficulty = NewerRopsten.Difficulty;
            statusMessage.BestHash = NewerRopsten.BestHash;
            statusMessage.GenesisHash = NewerRopsten.GenesisHash;
            //

            Logger.Log($"ETH sending status");
            _statusSent = true;
            Send(statusMessage);
        }

        public void Close()
        {
        }

        public event EventHandler SessionEstablished;
        public event EventHandler<ProtocolEventArgs> SubprotocolRequested;

        private static class TempRopstenSetup
        {
            public static BigInteger Difficulty { get; } = 0x100000; // 1,048,576
            public static Keccak GenesisHash { get; } = new Keccak(new Hex("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"));
            public static Keccak BestHash { get; } = new Keccak(new Hex("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"));
            public static int ChainId { get; } = 3;
        }
        
        private static class NewerRopsten // 2963492   
        {
            public static BigInteger Difficulty { get; } = 7984694325252517;
            public static Keccak GenesisHash { get; } = new Keccak(new Hex("0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d"));
            public static Keccak BestHash { get; } = new Keccak(new Hex("0x452a31d7627daa0a58e7bdcf4d3f9838e710b45220eb98b8c2cee5c71d5ed9aa"));
            public static int ChainId { get; } = 3;
        }
    }
}