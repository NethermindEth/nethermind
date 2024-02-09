// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public class SnapProtocolHandler : ZeroProtocolHandlerBase, ISnapSyncPeer
    {
        public static TimeSpan LowerLatencyThreshold = TimeSpan.FromMilliseconds(2000);
        public static TimeSpan UpperLatencyThreshold = TimeSpan.FromMilliseconds(3000);

        private readonly LatencyBasedRequestSizer _requestSizer = new(
            minRequestLimit: 50000,
            maxRequestLimit: 3_000_000,
            lowerWatermark: LowerLatencyThreshold,
            upperWatermark: UpperLatencyThreshold
        );

        protected ISnapServer? SyncServer { get; }
        protected IBackgroundTaskScheduler BackgroundTaskScheduler { get; }
        protected bool ServingEnabled { get; }

        public override string Name => "snap1";
        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => Protocol.Snap;
        public override int MessageIdSpaceSize => 8;

        private const string DisconnectMessage = "Serving snap data in not implemented in this node.";

        private readonly MessageQueue<GetAccountRangeMessage, AccountRangeMessage> _getAccountRangeRequests;
        private readonly MessageQueue<GetStorageRangeMessage, StorageRangeMessage> _getStorageRangeRequests;
        private readonly MessageQueue<GetByteCodesMessage, ByteCodesMessage> _getByteCodesRequests;
        private readonly MessageQueue<GetTrieNodesMessage, TrieNodesMessage> _getTrieNodesRequests;
        private static readonly byte[] _emptyBytes = { 0 };

        public SnapProtocolHandler(ISession session,
            INodeStatsManager nodeStats,
            IMessageSerializationService serializer,
            ISnapServer? snapServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ILogManager logManager)
            : base(session, nodeStats, serializer, logManager)
        {
            _getAccountRangeRequests = new(Send);
            _getStorageRangeRequests = new(Send);
            _getByteCodesRequests = new(Send);
            _getTrieNodesRequests = new(Send);
            SyncServer = snapServer;
            BackgroundTaskScheduler = backgroundTaskScheduler;
            ServingEnabled = SyncServer != null;
        }

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
        {
            add { }
            remove { }
        }

        public override void Init()
        {
            ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
        }

        public override void Dispose()
        {
        }

        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case SnapMessageCode.GetAccountRange:
                    if (!ServingEnabled)
                    {
                        Session.InitiateDisconnect(DisconnectReason.SnapServerNotImplemented, DisconnectMessage);
                        if (Logger.IsDebug)
                            Logger.Debug(
                                $"Peer disconnected because of requesting Snap data (GetAccountRange). Peer: {Session.Node.ClientId}");
                        break;
                    }
                    GetAccountRangeMessage getAccountRangeMessage = Deserialize<GetAccountRangeMessage>(message.Content);
                    ReportIn(getAccountRangeMessage, size);
                    ScheduleSyncServe(getAccountRangeMessage, Handle);
                    break;
                case SnapMessageCode.AccountRange:
                    AccountRangeMessage accountRangeMessage = Deserialize<AccountRangeMessage>(message.Content);
                    ReportIn(accountRangeMessage, size);
                    Handle(accountRangeMessage, size);
                    break;
                case SnapMessageCode.GetStorageRanges:
                    if (!ServingEnabled)
                    {
                        Session.InitiateDisconnect(DisconnectReason.SnapServerNotImplemented, DisconnectMessage);
                        if (Logger.IsDebug)
                            Logger.Debug(
                                $"Peer disconnected because of requesting Snap data (GetStorageRanges). Peer: {Session.Node.ClientId}");
                        break;
                    }
                    GetStorageRangeMessage getStorageRangesMessage = Deserialize<GetStorageRangeMessage>(message.Content);
                    ReportIn(getStorageRangesMessage, size);
                    ScheduleSyncServe(getStorageRangesMessage, Handle);
                    break;
                case SnapMessageCode.StorageRanges:
                    StorageRangeMessage storageRangesMessage = Deserialize<StorageRangeMessage>(message.Content);
                    ReportIn(storageRangesMessage, size);
                    Handle(storageRangesMessage, size);
                    break;
                case SnapMessageCode.GetByteCodes:
                    if (!ServingEnabled)
                    {
                        Session.InitiateDisconnect(DisconnectReason.SnapServerNotImplemented, DisconnectMessage);
                        if (Logger.IsDebug)
                            Logger.Debug(
                                $"Peer disconnected because of requesting Snap data (GetByteCodes). Peer: {Session.Node.ClientId}");
                        break;
                    }
                    GetByteCodesMessage getByteCodesMessage = Deserialize<GetByteCodesMessage>(message.Content);
                    ReportIn(getByteCodesMessage, size);
                    ScheduleSyncServe(getByteCodesMessage, Handle);
                    break;
                case SnapMessageCode.ByteCodes:
                    ByteCodesMessage byteCodesMessage = Deserialize<ByteCodesMessage>(message.Content);
                    ReportIn(byteCodesMessage, size);
                    Handle(byteCodesMessage, size);
                    break;
                case SnapMessageCode.GetTrieNodes:
                    if (!ServingEnabled)
                    {
                        Session.InitiateDisconnect(DisconnectReason.SnapServerNotImplemented, DisconnectMessage);
                        if (Logger.IsDebug)
                            Logger.Debug(
                                $"Peer disconnected because of requesting Snap data (GetTrieNodes). Peer: {Session.Node.ClientId}");
                        break;
                    }
                    GetTrieNodesMessage getTrieNodesMessage = Deserialize<GetTrieNodesMessage>(message.Content);
                    ReportIn(getTrieNodesMessage, size);
                    ScheduleSyncServe(getTrieNodesMessage, Handle);
                    break;
                case SnapMessageCode.TrieNodes:
                    TrieNodesMessage trieNodesMessage = Deserialize<TrieNodesMessage>(message.Content);
                    ReportIn(trieNodesMessage, size);
                    Handle(trieNodesMessage, size);
                    break;
            }
        }

        private void Handle(AccountRangeMessage msg, long size)
        {
            Metrics.SnapAccountRangeReceived++;
            _getAccountRangeRequests.Handle(msg, size);
        }

        private void Handle(StorageRangeMessage msg, long size)
        {
            Metrics.SnapStorageRangesReceived++;
            _getStorageRangeRequests.Handle(msg, size);
        }

        private void Handle(ByteCodesMessage msg, long size)
        {
            Metrics.SnapByteCodesReceived++;
            _getByteCodesRequests.Handle(msg, size);
        }

        private void Handle(TrieNodesMessage msg, long size)
        {
            Metrics.SnapTrieNodesReceived++;
            _getTrieNodesRequests.Handle(msg, size);
        }

        private Task<AccountRangeMessage> Handle(GetAccountRangeMessage getAccountRangeMessage, CancellationToken cancellationToken)
        {
            Metrics.SnapGetAccountRangeReceived++;
            AccountRangeMessage? response = FulfillAccountRangeMessage(getAccountRangeMessage, cancellationToken);
            response.RequestId = getAccountRangeMessage.RequestId;
            return Task.FromResult(response);
        }

        private Task<StorageRangeMessage> Handle(GetStorageRangeMessage getStorageRangesMessage, CancellationToken cancellationToken)
        {
            Metrics.SnapGetStorageRangesReceived++;
            StorageRangeMessage? response = FulfillStorageRangeMessage(getStorageRangesMessage, cancellationToken);
            response.RequestId = getStorageRangesMessage.RequestId;
            return Task.FromResult(response);
        }

        private Task<ByteCodesMessage> Handle(GetByteCodesMessage getByteCodesMessage, CancellationToken cancellationToken)
        {
            Metrics.SnapGetByteCodesReceived++;
            ByteCodesMessage? response = FulfillByteCodesMessage(getByteCodesMessage, cancellationToken);
            response.RequestId = getByteCodesMessage.RequestId;
            return Task.FromResult(response);
        }

        private Task<TrieNodesMessage> Handle(GetTrieNodesMessage getTrieNodesMessage, CancellationToken cancellationToken)
        {
            Metrics.SnapGetTrieNodesReceived++;
            TrieNodesMessage? response = FulfillTrieNodesMessage(getTrieNodesMessage, cancellationToken);
            response.RequestId = getTrieNodesMessage.RequestId;
            return Task.FromResult(response);
        }

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        protected TrieNodesMessage FulfillTrieNodesMessage(GetTrieNodesMessage getTrieNodesMessage, CancellationToken cancellationToken)
        {
            if (SyncServer == null) return new TrieNodesMessage(Array.Empty<byte[]>());
            var trieNodes = SyncServer.GetTrieNodes(getTrieNodesMessage.Paths, getTrieNodesMessage.RootHash, cancellationToken);
            return new TrieNodesMessage(trieNodes);
        }

        protected AccountRangeMessage FulfillAccountRangeMessage(GetAccountRangeMessage getAccountRangeMessage, CancellationToken cancellationToken)
        {
            if (SyncServer == null) return new AccountRangeMessage()
            {
                Proofs = Array.Empty<byte[]>(),
                PathsWithAccounts = Array.Empty<PathWithAccount>(),
            };
            AccountRange? accountRange = getAccountRangeMessage.AccountRange;
            (PathWithAccount[]? ranges, byte[][]? proofs) = SyncServer.GetAccountRanges(accountRange.RootHash, accountRange.StartingHash,
                accountRange.LimitHash, getAccountRangeMessage.ResponseBytes, cancellationToken);
            AccountRangeMessage? response = new() { Proofs = proofs, PathsWithAccounts = ranges };
            return response;
        }
        protected StorageRangeMessage FulfillStorageRangeMessage(GetStorageRangeMessage getStorageRangeMessage, CancellationToken cancellationToken)
        {
            if (SyncServer == null) return new StorageRangeMessage()
            {
                Proofs = Array.Empty<byte[]>(),
                Slots = Array.Empty<PathWithStorageSlot[]>(),
            };
            StorageRange? storageRange = getStorageRangeMessage.StoragetRange;
            (PathWithStorageSlot[][]? ranges, byte[][]? proofs) = SyncServer.GetStorageRanges(storageRange.RootHash, storageRange.Accounts,
                storageRange.StartingHash, storageRange.LimitHash, getStorageRangeMessage.ResponseBytes, cancellationToken);
            StorageRangeMessage? response = new() { Proofs = proofs, Slots = ranges };
            return response;
        }
        protected ByteCodesMessage FulfillByteCodesMessage(GetByteCodesMessage getByteCodesMessage, CancellationToken cancellationToken)
        {
            if (SyncServer == null) return new ByteCodesMessage(Array.Empty<byte[]>());
            var byteCodes = SyncServer.GetByteCodes(getByteCodesMessage.Hashes, getByteCodesMessage.Bytes, cancellationToken);
            return new ByteCodesMessage(byteCodes);
        }

        public async Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
        {
            AccountRangeMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
                SendRequest(new GetAccountRangeMessage()
                {
                    AccountRange = range,
                    ResponseBytes = bytesLimit
                }, _getAccountRangeRequests, token));

            Metrics.SnapGetAccountRangeSent++;

            return new AccountsAndProofs() { PathAndAccounts = response.PathsWithAccounts, Proofs = response.Proofs };
        }

        public async Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token)
        {
            StorageRangeMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
                SendRequest(new GetStorageRangeMessage()
                {
                    StoragetRange = range,
                    ResponseBytes = bytesLimit
                }, _getStorageRangeRequests, token));

            Metrics.SnapGetStorageRangesSent++;

            return new SlotsAndProofs() { PathsAndSlots = response.Slots, Proofs = response.Proofs };
        }

        public async Task<byte[][]> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
        {
            ByteCodesMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
                SendRequest(new GetByteCodesMessage()
                {
                    Hashes = codeHashes,
                    Bytes = bytesLimit,
                }, _getByteCodesRequests, token));

            Metrics.SnapGetByteCodesSent++;

            return response.Codes;
        }

        public async Task<byte[][]> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
        {
            PathGroup[] groups = GetPathGroups(request);

            return await GetTrieNodes(request.RootHash, groups, token);
        }

        public async Task<byte[][]> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
        {
            return await GetTrieNodes(request.RootHash, request.AccountAndStoragePaths, token);
        }

        private async Task<byte[][]> GetTrieNodes(ValueHash256 rootHash, PathGroup[] groups, CancellationToken token)
        {
            TrieNodesMessage response = await _requestSizer.MeasureLatency((bytesLimit) =>
                SendRequest(new GetTrieNodesMessage()
                {
                    RootHash = rootHash,
                    Paths = groups,
                    Bytes = bytesLimit
                }, _getTrieNodesRequests, token));

            Metrics.SnapGetTrieNodesSent++;

            return response.Nodes;
        }

        private static PathGroup[] GetPathGroups(AccountsToRefreshRequest request)
        {
            PathGroup[] groups = new PathGroup[request.Paths.Length];

            for (int i = 0; i < request.Paths.Length; i++)
            {
                AccountWithStorageStartingHash path = request.Paths[i];
                groups[i] = new PathGroup() { Group = new[] { path.PathAndAccount.Path.Bytes.ToArray(), _emptyBytes } };
            }

            return groups;
        }

        private async Task<TOut> SendRequest<TIn, TOut>(TIn msg, MessageQueue<TIn, TOut> requestQueue, CancellationToken token)
            where TIn : SnapMessageBase
            where TOut : SnapMessageBase
        {
            return await SendRequestGeneric(
                requestQueue,
                msg,
                TransferSpeedType.SnapRanges,
                static (request) => request.ToString(),
                token);
        }

        protected void ScheduleSyncServe<TReq, TRes>(TReq request, Func<TReq, CancellationToken, Task<TRes>> fulfillFunc) where TRes : P2PMessage
        {
            BackgroundTaskScheduler.ScheduleTask((request, fulfillFunc), BackgroundSyncSender);
        }

        // I just don't want to create a closure.. so this happens.
        private async Task BackgroundSyncSender<TReq, TRes>(
            (TReq Request, Func<TReq, CancellationToken, Task<TRes>> FullfillFunc) input, CancellationToken cancellationToken) where TRes : P2PMessage
        {
            try
            {
                TRes response = await input.FullfillFunc.Invoke(input.Request, cancellationToken);
                Send(response);
            }
            catch (EthSyncException e)
            {
                Session.InitiateDisconnect(DisconnectReason.EthSyncException, e.Message);
            }
        }

    }
}
