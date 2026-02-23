// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Xdc.P2P.Eth100.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P.Eth100
{
    /// <summary>
    /// XDC Network eth/100 protocol handler
    /// Extends standard Ethereum eth/63 with XDPoS v2 consensus messages
    /// Uses eth/62-style handshake WITHOUT ForkID (XDC specific)
    /// </summary>
    public class Eth100ProtocolHandler : Eth63ProtocolHandler
    {
        private readonly IXdcConsensusMessageProcessor? _consensusProcessor;
        private Timer? _keepaliveTimer;
        private readonly TimeSpan _keepaliveInterval = TimeSpan.FromSeconds(20);
        private DateTime _lastActivity = DateTime.MinValue;
        
        /// <summary>
        /// Event fired when XDC peer disconnects - can be used for immediate reconnection attempts
        /// </summary>
        public event EventHandler<DisconnectEventArgs>? XdcPeerDisconnected;

        public Eth100ProtocolHandler(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            ILogManager logManager,
            IXdcConsensusMessageProcessor? consensusProcessor = null,
            ITxGossipPolicy? transactionsGossipPolicy = null)
            : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, 
                   txPool, gossipPolicy, logManager, transactionsGossipPolicy)
        {
            _consensusProcessor = consensusProcessor;
        }

        public override byte ProtocolVersion => 100; // eth/100

        public override string Name => "eth100";

        public override int MessageIdSpaceSize => 227; // 0x00-0xe2 (eth/63 + XDPoS consensus at 0xe0-0xe2)

        /// <summary>
        /// Override to prevent ForkID from being added (XDC uses eth/62-style status)
        /// XDC eth/100 does NOT use ForkID - uses eth/62-style handshake
        /// </summary>
        protected override void EnrichStatusMessage(StatusMessage statusMessage)
        {
            // Intentionally empty - XDC eth/100 does NOT use ForkID
            // This ensures compatibility with XDC's pre-merge, eth/62-style handshake
            if (Logger.IsDebug)
                Logger.Debug("XDC eth/100: Skipping ForkID in status message (eth/62-style handshake)");
        }

        /// <summary>
        /// Initialize the protocol and send status message to peer
        /// </summary>
        public override void Init()
        {
            if (Logger.IsDebug)
                Logger.Debug($"XDC eth/100: Initializing protocol with {Node:c}");

            base.Init();

            // Subscribe to disconnect events for reconnection signaling
            if (Session != null)
            {
                Session.Disconnected += OnSessionDisconnected;
            }

            // Start keepalive timer to prevent go-ethereum's 30-second frame read timeout
            // XDC nodes (go-ethereum fork) disconnect idle peers after frameReadTimeout (30s)
            // We send a lightweight GetBlockHeaders request every 20s to keep the connection alive
            _lastActivity = DateTime.UtcNow;
            _keepaliveTimer = new Timer(OnKeepaliveTimer, null, _keepaliveInterval, _keepaliveInterval);
            
            if (Logger.IsDebug)
                Logger.Debug($"XDC eth/100: Keepalive timer started ({_keepaliveInterval.TotalSeconds}s interval)");

            if (Logger.IsDebug)
                Logger.Debug($"XDC eth/100: Protocol initialized with {Node:c}");
        }

        public override void Dispose()
        {
            // Unsubscribe from disconnect events
            if (Session != null)
            {
                Session.Disconnected -= OnSessionDisconnected;
            }
            
            _keepaliveTimer?.Dispose();
            _keepaliveTimer = null;
            base.Dispose();
        }

        /// <summary>
        /// Handle session disconnect - log and signal for immediate reconnection
        /// </summary>
        private void OnSessionDisconnected(object? sender, DisconnectEventArgs e)
        {
            if (Logger.IsInfo)
            {
                Logger.Info($"XDC eth/100: Peer {Node:c} disconnected. " +
                           $"Reason: {e.DisconnectReason}, Type: {e.DisconnectType}. " +
                           $"Signaling for immediate reconnection...");
            }

            // Fire event for external reconnection handlers (e.g., peer pool manager)
            // This allows the peer manager to immediately re-queue this XDC peer
            // instead of waiting for the normal discovery cycle
            try
            {
                XdcPeerDisconnected?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                if (Logger.IsDebug)
                    Logger.Debug($"XDC eth/100: Error in disconnect handler: {ex.Message}");
            }
        }

        public override void HandleMessage(ZeroPacket message)
        {
            _lastActivity = DateTime.UtcNow;
            int size = message.Content.ReadableBytes;

            // Handle XDPoS v2 messages
            switch (message.PacketType)
            {
                case Eth100MessageCode.Vote:
                    VoteP2PMessage voteMessage = Deserialize<VoteP2PMessage>(message.Content);
                    ReportIn(voteMessage, size);
                    Handle(voteMessage);
                    break;

                case Eth100MessageCode.Timeout:
                    TimeoutP2PMessage timeoutMessage = Deserialize<TimeoutP2PMessage>(message.Content);
                    ReportIn(timeoutMessage, size);
                    Handle(timeoutMessage);
                    break;

                case Eth100MessageCode.SyncInfo:
                    SyncInfoP2PMessage syncInfoMessage = Deserialize<SyncInfoP2PMessage>(message.Content);
                    ReportIn(syncInfoMessage, size);
                    Handle(syncInfoMessage);
                    break;

                default:
                    // Note: QuorumCertificate is embedded in Vote/Timeout/SyncInfo in geth-xdc,
                    // not a standalone message
                    // Delegate to base class for standard eth/63 messages
                    base.HandleMessage(message);
                    break;
            }
        }

        /// <summary>
        /// Keepalive timer callback - sends a lightweight request to keep the connection alive
        /// go-ethereum (XDC) has frameReadTimeout = 30s, so we send traffic every 20s
        /// </summary>
        private void OnKeepaliveTimer(object? state)
        {
            try
            {
                // Only send keepalive if connection is idle for more than 15 seconds
                // This prevents duplicate requests if there's already active sync traffic
                if (DateTime.UtcNow - _lastActivity < TimeSpan.FromSeconds(15))
                {
                    return;
                }

                if (Session?.State != SessionState.Initialized)
                {
                    return;
                }

                // Send a lightweight GetBlockHeaders request for the peer's head block
                // This is a valid request that XDC nodes can handle and keeps the connection alive
                var headHash = HeadHash;
                if (headHash != null)
                {
                    if (Logger.IsTrace)
                        Logger.Trace($"XDC eth/100: Sending keepalive GetBlockHeaders to {Node:c}");

                    // Fire-and-forget - we don't need the response, just need to send traffic
                    _ = SendKeepaliveRequest(headHash);

                    _lastActivity = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Keepalive failures are non-critical - just log at trace level
                if (Logger.IsTrace)
                    Logger.Trace($"XDC eth/100: Keepalive timer error (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a lightweight keepalive request to prevent connection timeout
        /// </summary>
        private async Task SendKeepaliveRequest(Hash256 headHash)
        {
            try
            {
                // Send a GetBlockHeaders request for just 1 header - minimal overhead
                var msg = new GetBlockHeadersMessage
                {
                    StartBlockHash = headHash,
                    MaxHeaders = 1,
                    Skip = 0,
                    Reverse = 0
                };

                await SendRequest(msg, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Keepalive failures are non-critical - peer might be busy or disconnected
                if (Logger.IsTrace)
                    Logger.Trace($"XDC eth/100: Keepalive request failed (expected if peer busy): {ex.Message}");
            }
        }

        protected virtual void Handle(VoteP2PMessage msg)
        {
            if (Logger.IsTrace)
                Logger.Trace($"Received Vote from {Node:c}: {msg.Vote}");

            _consensusProcessor?.ProcessVote(msg.Vote);
        }

        protected virtual void Handle(TimeoutP2PMessage msg)
        {
            if (Logger.IsTrace)
                Logger.Trace($"Received Timeout from {Node:c}: {msg.Timeout}");

            _consensusProcessor?.ProcessTimeout(msg.Timeout);
        }

        protected virtual void Handle(SyncInfoP2PMessage msg)
        {
            if (Logger.IsTrace)
                Logger.Trace($"Received SyncInfo from {Node:c}");

            _consensusProcessor?.ProcessSyncInfo(msg.SyncInfo);
        }

        protected virtual void Handle(QuorumCertificateP2PMessage msg)
        {
            if (Logger.IsTrace)
                Logger.Trace($"Received QuorumCertificate from {Node:c}: {msg.QuorumCertificate?.ProposedBlockInfo}");

            _consensusProcessor?.ProcessQuorumCertificate(msg.QuorumCertificate);
        }

        /// <summary>
        /// Broadcast a vote to all connected peers
        /// </summary>
        public void BroadcastVote(Nethermind.Xdc.Types.Vote vote)
        {
            VoteP2PMessage message = new(vote);
            Send(message);
            
            if (Logger.IsDebug)
                Logger.Debug($"Broadcast Vote: {vote}");
        }

        /// <summary>
        /// Broadcast a timeout to all connected peers
        /// </summary>
        public void BroadcastTimeout(Nethermind.Xdc.Types.Timeout timeout)
        {
            TimeoutP2PMessage message = new(timeout);
            Send(message);
            
            if (Logger.IsDebug)
                Logger.Debug($"Broadcast Timeout: {timeout}");
        }

        /// <summary>
        /// Send sync info to a specific peer
        /// </summary>
        public void SendSyncInfo(Nethermind.Xdc.Types.SyncInfo syncInfo)
        {
            SyncInfoP2PMessage message = new(syncInfo);
            Send(message);
            
            if (Logger.IsTrace)
                Logger.Trace($"Sent SyncInfo to {Node:c}");
        }

        /// <summary>
        /// Broadcast a quorum certificate to all connected peers
        /// </summary>
        public void BroadcastQuorumCertificate(Nethermind.Xdc.Types.QuorumCertificate qc)
        {
            QuorumCertificateP2PMessage message = new(qc);
            Send(message);
            
            if (Logger.IsDebug)
                Logger.Debug($"Broadcast QC: {qc?.ProposedBlockInfo}");
        }
    }
}
