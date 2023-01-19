// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V64
{
    /// <summary>
    /// https://eips.ethereum.org/EIPS/eip-2364
    /// </summary>
    public class Eth64ProtocolHandler : Eth63ProtocolHandler
    {
        private readonly ForkInfo _forkInfo;

        public Eth64ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            ForkInfo forkInfo,
            ILogManager logManager) : base(session, serializer, nodeStatsManager, syncServer, txPool, gossipPolicy, logManager)
        {
            _forkInfo = forkInfo ?? throw new ArgumentNullException(nameof(forkInfo));
        }

        public override string Name => "eth64";

        public override byte ProtocolVersion => 64;

        protected override void EnrichStatusMessage(StatusMessage statusMessage)
        {
            base.EnrichStatusMessage(statusMessage);
            statusMessage.ForkId = _forkInfo.GetForkId(SyncServer.Head!.Number, SyncServer.Head.Timestamp);
        }
    }
}
