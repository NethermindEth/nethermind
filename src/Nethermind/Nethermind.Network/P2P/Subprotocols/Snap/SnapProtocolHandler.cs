// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public class SnapProtocolHandler : ZeroProtocolHandlerBase, ISnapSyncPeer
    {
        private static readonly TrieNodesMessage EmptyTrieNodesMessage = new TrieNodesMessage(ArrayPoolList<byte[]>.Empty());

        private ISnapServer? SyncServer { get; }
        private BackgroundTaskSchedulerWrapper BackgroundTaskScheduler { get; }
        private bool ServingEnabled { get; }

        public override string Name => "snap1";
        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => Protocol.Snap;
        public override int MessageIdSpaceSize => 8;

        private const string DisconnectMessage = "Serving snap data in not implemented in this node.";

        private readonly MessageDictionary<GetAccountRangeMessage, AccountRangeMessage> _getAccountRangeRequests;
        private readonly MessageDictionary<GetStorageRangeMessage, StorageRangeMessage> _getStorageRangeRequests;
        private readonly MessageDictionary<GetByteCodesMessage, ByteCodesMessage> _getByteCodesRequests;
        private readonly MessageDictionary<GetTrieNodesMessage, TrieNodesMessage> _getTrieNodesRequests;
        private static readonly byte[] _emptyBytes = [0];

        public SnapProtocolHandler(ISession session,
            INodeStatsManager nodeStats,
            IMessageSerializationService serializer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ILogManager logManager,
            ISnapServer? snapServer = null)
            : base(session, nodeStats, serializer, logManager)
        {
            _getAccountRangeRequests = new(Send);
            _getStorageRangeRequests = new(Send);
            _getByteCodesRequests = new(Send);
            _getTrieNodesRequests = new(Send);
            SyncServer = snapServer;
            BackgroundTaskScheduler = new BackgroundTaskSchedulerWrapper(this, backgroundTaskScheduler);
            ServingEnabled = SyncServer is not null;
        }

        public override event EventHandler<ProtocolInitializedEventArgs>? ProtocolInitialized;
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
            // Clear Events if set
            ProtocolInitialized = null;
        }

        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case SnapMessageCode.GetAccountRange:
                    if (ShouldServeSnap(nameof(GetAccountRangeMessage)))
                    {
                        GetAccountRangeMessage getAccountRangeMessage = Deserialize<GetAccountRangeMessage>(message.Content);
                        ReportIn(getAccountRangeMessage, size);
                        BackgroundTaskScheduler.ScheduleSyncServe(getAccountRangeMessage, Handle);
                    }

                    break;
                case SnapMessageCode.AccountRange:
                    AccountRangeMessage accountRangeMessage = Deserialize<AccountRangeMessage>(message.Content);
                    ReportIn(accountRangeMessage, size);
                    Handle(accountRangeMessage, size);
                    break;
                case SnapMessageCode.GetStorageRanges:
                    if (ShouldServeSnap(nameof(GetStorageRangeMessage)))
                    {
                        GetStorageRangeMessage getStorageRangesMessage = Deserialize<GetStorageRangeMessage>(message.Content);
                        ReportIn(getStorageRangesMessage, size);
                        BackgroundTaskScheduler.ScheduleSyncServe(getStorageRangesMessage, Handle);
                    }

                    break;
                case SnapMessageCode.StorageRanges:
                    StorageRangeMessage storageRangesMessage = Deserialize<StorageRangeMessage>(message.Content);
                    ReportIn(storageRangesMessage, size);
                    Handle(storageRangesMessage, size);
                    break;
                case SnapMessageCode.GetByteCodes:
                    if (ShouldServeSnap(nameof(GetByteCodesMessage)))
                    {
                        GetByteCodesMessage getByteCodesMessage = Deserialize<GetByteCodesMessage>(message.Content);
                        ReportIn(getByteCodesMessage, size);
                        BackgroundTaskScheduler.ScheduleSyncServe(getByteCodesMessage, Handle);
                    }

                    break;
                case SnapMessageCode.ByteCodes:
                    ByteCodesMessage byteCodesMessage = Deserialize<ByteCodesMessage>(message.Content);
                    ReportIn(byteCodesMessage, size);
                    Handle(byteCodesMessage, size);
                    break;
                case SnapMessageCode.GetTrieNodes:
                    if (ShouldServeSnap(nameof(GetTrieNodes)))
                    {
                        GetTrieNodesMessage getTrieNodesMessage = Deserialize<GetTrieNodesMessage>(message.Content);
                        ReportIn(getTrieNodesMessage, size);
                        BackgroundTaskScheduler.ScheduleSyncServe(getTrieNodesMessage, Handle);
                    }

                    break;
                case SnapMessageCode.TrieNodes:
                    TrieNodesMessage trieNodesMessage = Deserialize<TrieNodesMessage>(message.Content);
                    ReportIn(trieNodesMessage, size);
                    Handle(trieNodesMessage, size);
                    break;
            }
        }

        private bool ShouldServeSnap(string messageName)
        {
            if (!ServingEnabled)
            {
                Session.InitiateDisconnect(DisconnectReason.SnapServerNotImplemented, DisconnectMessage);
                if (Logger.IsDebug)
                    Logger.Debug($"Peer disconnected because of requesting Snap data ({messageName}). Peer: {Session.Node.ClientId}");
                return false;
            }

            return true;
        }

        private void Handle(AccountRangeMessage msg, long size)
        {
            _getAccountRangeRequests.Handle(msg.RequestId, msg, size);
        }

        private void Handle(StorageRangeMessage msg, long size)
        {
            _getStorageRangeRequests.Handle(msg.RequestId, msg, size);
        }

        private void Handle(ByteCodesMessage msg, long size)
        {
            _getByteCodesRequests.Handle(msg.RequestId, msg, size);
        }

        private void Handle(TrieNodesMessage msg, long size)
        {
            _getTrieNodesRequests.Handle(msg.RequestId, msg, size);
        }

        private ValueTask<AccountRangeMessage> Handle(GetAccountRangeMessage getAccountRangeMessage, CancellationToken cancellationToken)
        {
            using GetAccountRangeMessage message = getAccountRangeMessage;
            AccountRangeMessage? response = FulfillAccountRangeMessage(message, cancellationToken);
            response.RequestId = message.RequestId;
            return new ValueTask<AccountRangeMessage>(response);
        }

        private ValueTask<StorageRangeMessage> Handle(GetStorageRangeMessage getStorageRangesMessage, CancellationToken cancellationToken)
        {
            using GetStorageRangeMessage message = getStorageRangesMessage;
            StorageRangeMessage? response = FulfillStorageRangeMessage(message, cancellationToken);
            response.RequestId = message.RequestId;
            return new ValueTask<StorageRangeMessage>(response);
        }

        private ValueTask<ByteCodesMessage> Handle(GetByteCodesMessage getByteCodesMessage, CancellationToken cancellationToken)
        {
            using GetByteCodesMessage message = getByteCodesMessage;
            ByteCodesMessage? response = FulfillByteCodesMessage(message, cancellationToken);
            response.RequestId = message.RequestId;
            return new ValueTask<ByteCodesMessage>(response);
        }

        private ValueTask<TrieNodesMessage> Handle(GetTrieNodesMessage getTrieNodesMessage, CancellationToken cancellationToken)
        {
            using GetTrieNodesMessage message = getTrieNodesMessage;
            TrieNodesMessage? response = FulfillTrieNodesMessage(message, cancellationToken);
            response.RequestId = message.RequestId;
            return new ValueTask<TrieNodesMessage>(response);
        }

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        private TrieNodesMessage FulfillTrieNodesMessage(GetTrieNodesMessage getTrieNodesMessage, CancellationToken cancellationToken)
        {
            if (SyncServer is null) return EmptyTrieNodesMessage;
            IOwnedReadOnlyList<byte[]>? trieNodes = SyncServer.GetTrieNodes(getTrieNodesMessage.Paths, getTrieNodesMessage.RootHash, cancellationToken);
            return new TrieNodesMessage(trieNodes);
        }

        private AccountRangeMessage FulfillAccountRangeMessage(GetAccountRangeMessage getAccountRangeMessage, CancellationToken cancellationToken)
        {
            if (SyncServer is null) return new AccountRangeMessage
            {
                Proofs = ArrayPoolList<byte[]>.Empty(),
                PathsWithAccounts = ArrayPoolList<PathWithAccount>.Empty(),
            };
            AccountRange? accountRange = getAccountRangeMessage.AccountRange;
            (IOwnedReadOnlyList<PathWithAccount>? ranges, IOwnedReadOnlyList<byte[]>? proofs) = SyncServer.GetAccountRanges(accountRange.RootHash, accountRange.StartingHash,
                accountRange.LimitHash, getAccountRangeMessage.ResponseBytes, cancellationToken);
            AccountRangeMessage? response = new() { Proofs = proofs, PathsWithAccounts = ranges };
            return response;
        }

        private StorageRangeMessage FulfillStorageRangeMessage(GetStorageRangeMessage getStorageRangeMessage, CancellationToken cancellationToken)
        {
            if (SyncServer is null) return new StorageRangeMessage()
            {
                Proofs = ArrayPoolList<byte[]>.Empty(),
                Slots = ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(),
            };
            StorageRange? storageRange = getStorageRangeMessage.StorageRange;
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>? ranges, IOwnedReadOnlyList<byte[]> proofs) = SyncServer.GetStorageRanges(storageRange.RootHash, storageRange.Accounts,
                storageRange.StartingHash, storageRange.LimitHash, getStorageRangeMessage.ResponseBytes, cancellationToken);
            StorageRangeMessage? response = new() { Proofs = proofs, Slots = ranges };
            return response;
        }

        private ByteCodesMessage FulfillByteCodesMessage(GetByteCodesMessage getByteCodesMessage, CancellationToken cancellationToken)
        {
            if (SyncServer is null) return new ByteCodesMessage(ArrayPoolList<byte[]>.Empty());
            IOwnedReadOnlyList<byte[]>? byteCodes = SyncServer.GetByteCodes(getByteCodesMessage.Hashes, getByteCodesMessage.Bytes, cancellationToken);
            return new ByteCodesMessage(byteCodes);
        }

        public async Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
        {
            AccountRangeMessage response = await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
                SendRequest(new GetAccountRangeMessage()
                {
                    AccountRange = range,
                    ResponseBytes = bytesLimit
                }, _getAccountRangeRequests, token));

            return new AccountsAndProofs { PathAndAccounts = response.PathsWithAccounts, Proofs = response.Proofs };
        }

        public async Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token)
        {
            StorageRangeMessage response = await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
                SendRequest(new GetStorageRangeMessage()
                {
                    StorageRange = range,
                    ResponseBytes = bytesLimit
                }, _getStorageRangeRequests, token));

            return new SlotsAndProofs { PathsAndSlots = response.Slots, Proofs = response.Proofs };
        }

        public async Task<IOwnedReadOnlyList<byte[]>> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
        {
            ByteCodesMessage response = await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
                SendRequest(new GetByteCodesMessage
                {
                    Hashes = codeHashes.ToPooledList(),
                    Bytes = bytesLimit,
                }, _getByteCodesRequests, token));

            return response.Codes;
        }

        public async Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
        {
            IOwnedReadOnlyList<PathGroup> groups = GetPathGroups(request);

            return await GetTrieNodes(request.RootHash, groups, token);
        }

        public async Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
        {
            return await GetTrieNodes(request.RootHash, request.AccountAndStoragePaths, token);
        }

        private async Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(Hash256 rootHash, IOwnedReadOnlyList<PathGroup> groups, CancellationToken token)
        {
            TrieNodesMessage response = await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
                SendRequest(new GetTrieNodesMessage
                {
                    RootHash = rootHash,
                    Paths = groups,
                    Bytes = bytesLimit
                }, _getTrieNodesRequests, token));

            return response.Nodes;
        }

        public static IOwnedReadOnlyList<PathGroup> GetPathGroups(AccountsToRefreshRequest request)
        {
            ArrayPoolList<PathGroup> groups = new(request.Paths.Count);

            for (int i = 0; i < request.Paths.Count; i++)
            {
                AccountWithStorageStartingHash path = request.Paths[i];
                groups.Add(new PathGroup { Group = [path.PathAndAccount.Path.Bytes.ToArray(), _emptyBytes] });
            }

            return groups;
        }

        private async Task<TOut> SendRequest<TIn, TOut>(TIn msg, MessageDictionary<TIn, TOut> messageDictionary, CancellationToken token)
            where TIn : SnapMessageBase
            where TOut : SnapMessageBase
        {
            Request<TIn, TOut> request = new(msg);
            messageDictionary.Send(request);

            return await HandleResponse(request, TransferSpeedType.SnapRanges, static req => req.ToString(), token);
        }
    }
}
