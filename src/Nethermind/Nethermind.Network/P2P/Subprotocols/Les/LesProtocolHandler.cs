using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class LesProtocolHandler : SyncPeerProtocolHandlerBase, IZeroProtocolHandler, ISyncPeer
    {
        public override string Name => "les3";

        public LesProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            ILogManager logManager,
            ITxPool txPool): base(session, serializer, statsManager, syncServer, logManager, txPool)
        {
            
        }

        public override byte ProtocolVersion { get; protected set; } = 3;

        public override string ProtocolCode => Protocol.Les;

        public override int MessageIdSpaceSize => 8;

        protected override TimeSpan InitTimeout => Timeouts.Les3Status;


        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
         {
            add { }
            remove { }
        }

        public override void AddSupportedCapability(Capability capability) { }

        public override void HandleMessage(ZeroPacket message)
        {
            throw new NotImplementedException();
        }

        public override bool HasAgreedCapability(Capability capability) => false;

        public override bool HasAvailableCapability(Capability capability) => false;

        public override void Init()
        {
            throw new NotImplementedException();
        }

        protected override void OnDisposed()
        {
            throw new NotImplementedException();
        }
    }
}
