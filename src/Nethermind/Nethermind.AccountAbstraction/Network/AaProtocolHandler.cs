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
// 

using System;
using System.Collections.Generic;
using DotNetty.Common.Utilities;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Timeouts = Nethermind.Network.Timeouts;

namespace Nethermind.AccountAbstraction.Network
{
    public class AaProtocolHandler : ProtocolHandlerBase, IZeroProtocolHandler, IUserOperationPoolPeer
    {
        private readonly ISession _session;
        private IDictionary<Address, IUserOperationPool> _userOperationPools;
        private readonly IAccountAbstractionPeerManager _peerManager;

        public AaProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            IDictionary<Address, IUserOperationPool> userOperationPools,
            IAccountAbstractionPeerManager peerManager,
            ILogManager logManager)
            : base(session, nodeStatsManager, serializer, logManager)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _userOperationPools = userOperationPools ?? throw new ArgumentNullException(nameof(userOperationPools));
            _peerManager = peerManager;
            
            IsPriority = _peerManager.NumberOfPriorityAaPeers > 0;
        }

        public PublicKey Id => _session.Node.Id;

        public override byte ProtocolVersion => 0;

        public override string ProtocolCode => Protocol.AA;
        
        public override int MessageIdSpaceSize => 4;

        public override string Name => "aa";

        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public override event EventHandler<ProtocolInitializedEventArgs>? ProtocolInitialized;

        public override event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }

        public override void Init()
        {
            ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));

            _peerManager.AddPeer(this);
            _session.Disconnected += SessionDisconnected;
        }

        private void SessionDisconnected(object? sender, DisconnectEventArgs e)
        {
            _peerManager.RemovePeer(Id);
            _session.Disconnected -= SessionDisconnected;
        }

        public override void HandleMessage(Packet message)
        {
            ZeroPacket zeroPacket = new(message);
            try
            {
                HandleMessage(zeroPacket);
            }
            finally
            {
                zeroPacket.SafeRelease();
            }
        }

        public void HandleMessage(ZeroPacket message)
        {
            switch (message.PacketType)
            {
                case AaMessageCode.UserOperations:
                    Metrics.UserOperationsMessagesReceived++;
                    UserOperationsMessage uopMsg = Deserialize<UserOperationsMessage>(message.Content);
                    ReportIn(uopMsg);
                    Handle(uopMsg);
                    break;
            }
        }

        private void Handle(UserOperationsMessage uopMsg)
        {
            IList<UserOperationWithEntryPoint> userOperations = uopMsg.UserOperationsWithEntryPoint;
            for (int i = 0; i < userOperations.Count; i++)
            {
                UserOperationWithEntryPoint uop = userOperations[i];
                if (_userOperationPools.TryGetValue(uop.EntryPoint, out IUserOperationPool? pool))
                {
                    ResultWrapper<Keccak> result = pool.AddUserOperation(uop.UserOperation);
                    if (Logger.IsTrace) Logger.Trace($"{_session.Node:c} sent {uop.UserOperation.RequestId!} uop to pool for entryPoint {uop.EntryPoint} and it was {result}");
                }
                else
                {
                    if (Logger.IsTrace) Logger.Trace($"{_session.Node:c} could not sent {uop.UserOperation.RequestId!} uop to pool for entryPoint {uop.EntryPoint}, pool does not support the entryPoint");
                }
            }
        }

        public void SendNewUserOperation(UserOperationWithEntryPoint uop)
        {
            SendMessage(new[] { uop });
        }

        public void SendNewUserOperations(IEnumerable<UserOperationWithEntryPoint> uops)
        {
            const int maxCapacity = 256;
            using ArrayPoolList<UserOperationWithEntryPoint> uopsToSend = new(maxCapacity);

            foreach (UserOperationWithEntryPoint uop in uops)
            {
                if (uopsToSend.Count == maxCapacity)
                {
                    SendMessage(uopsToSend);
                    uopsToSend.Clear();
                }

                // TODO: Why this check
                if (uop.UserOperation.RequestId is not null)
                {
                    uopsToSend.Add(uop);
                    TxPool.Metrics.PendingTransactionsSent++;
                }
            }

            if (uopsToSend.Count > 0)
            {
                SendMessage(uopsToSend);
            }
        }

        private void SendMessage(IList<UserOperationWithEntryPoint> uopsToSend)
        {
            UserOperationsMessage msg = new(uopsToSend);
            Send(msg);
            Metrics.UserOperationsMessagesSent++;
            if (Logger.IsTrace) Logger.Trace($"Sent {uopsToSend.Count} uops to {_session.Node:c}");
        }

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            if (Logger.IsDebug) Logger.Debug($"AA network protocol disconnected because of {disconnectReason} {details}");
            Dispose();
        }

        public override void Dispose() { }
    }
}
