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

using System;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
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
        private readonly ISpecProvider _specProvider;

        public Eth64ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            ISpecProvider specProvider,
            ILogManager logManager) : base(session, serializer, nodeStatsManager, syncServer, txPool, logManager)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }
        
        public override string Name => "eth64";
        
        public override byte ProtocolVersion => 64;

        protected override void EnrichStatusMessage(StatusMessage statusMessage)
        {
            base.EnrichStatusMessage(statusMessage);
            statusMessage.ForkId =
                ForkInfo.CalculateForkId(_specProvider, SyncServer.Head.Number, SyncServer.Genesis.Hash);
        }
    }
}
