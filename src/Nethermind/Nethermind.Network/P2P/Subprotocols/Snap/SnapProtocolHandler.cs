// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public class SnapProtocolHandler : ZeroProtocolHandlerBase, ISnapSyncPeer, IStaticProtocolInfo
    {
        private ISnapServer SyncServer { get; }
        private bool CanServe { get; }

        public override string Name => "snap1";
        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public static byte Version => 1;
        public static string Code => Protocol.Snap;
        public override byte ProtocolVersion => Version;
        public override string ProtocolCode => Code;
        public override int MessageIdSpaceSize => 8;

        private const string DisconnectMessage = "Serving snap data is not implemented in this node.";

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
            ISyncConfig syncConfig,
            ISnapServer snapServer)
            : base(session, nodeStats, serializer, backgroundTaskScheduler, logManager)
        {
            _getAccountRangeRequests = new(Send);
            _getStorageRangeRequests = new(Send);
            _getByteCodesRequests = new(Send);
            _getTrieNodesRequests = new(Send);
            SyncServer = snapServer;
            CanServe = snapServer.CanServe;
            SnapMessageLimits.GetTrieNodesPathsPerGroupRlpLimit = RlpLimit.For<PathGroup>(syncConfig.SnapServingMaxPathsPerGroup, nameof(PathGroup.Group));
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
                    if (ShouldServeSnap())
                        HandleInBackground<GetAccountRangeMessage, AccountRangeMessage>(message, Handle);
                    break;
                case SnapMessageCode.AccountRange:
                    AccountRangeMessage accountRangeMessage = Deserialize<AccountRangeMessage>(message.Content);
                    ReportIn(accountRangeMessage, size);
                    Handle(accountRangeMessage, size);
                    break;
                case SnapMessageCode.GetStorageRanges:
                    if (ShouldServeSnap())
                        HandleInBackground<GetStorageRangeMessage, StorageRangeMessage>(message, Handle);
                    break;
                case SnapMessageCode.StorageRanges:
                    StorageRangeMessage storageRangesMessage = Deserialize<StorageRangeMessage>(message.Content);
                    ReportIn(storageRangesMessage, size);
                    Handle(storageRangesMessage, size);
                    break;
                case SnapMessageCode.GetByteCodes:
                    if (ShouldServeSnap())
                        HandleInBackground<GetByteCodesMessage, ByteCodesMessage>(message, Handle);
                    break;
                case SnapMessageCode.ByteCodes:
                    ByteCodesMessage byteCodesMessage = Deserialize<ByteCodesMessage>(message.Content);
                    ReportIn(byteCodesMessage, size);
                    Handle(byteCodesMessage, size);
                    break;
                case SnapMessageCode.GetTrieNodes:
                    if (ShouldServeSnap())
                        HandleInBackground<GetTrieNodesMessage, TrieNodesMessage>(message, Handle);
                    break;
                case SnapMessageCode.TrieNodes:
                    TrieNodesMessage trieNodesMessage = Deserialize<TrieNodesMessage>(message.Content);
                    ReportIn(trieNodesMessage, size);
                    Handle(trieNodesMessage, size);
                    break;
            }
        }

        private bool ShouldServeSnap()
        {
            if (!CanServe)
            {
                Session.InitiateDisconnect(DisconnectReason.SnapServerNotImplemented, DisconnectMessage);
                if (Logger.IsDebug)
                    Logger.Debug($"Peer disconnected because of requesting Snap data. Peer: {Session.Node.ClientId}");
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
            IByteArrayList? trieNodes = SyncServer.GetTrieNodes(getTrieNodesMessage.Paths, getTrieNodesMessage.RootHash, cancellationToken);
            return new TrieNodesMessage(trieNodes);
        }

        private AccountRangeMessage FulfillAccountRangeMessage(GetAccountRangeMessage getAccountRangeMessage, CancellationToken cancellationToken)
        {
            AccountRange? accountRange = getAccountRangeMessage.AccountRange;
            (IOwnedReadOnlyList<PathWithAccount>? ranges, IByteArrayList? proofs) = SyncServer.GetAccountRanges(accountRange.RootHash, accountRange.StartingHash,
                accountRange.LimitHash, getAccountRangeMessage.ResponseBytes, cancellationToken);
            return new AccountRangeMessage { Proofs = proofs, PathsWithAccounts = ranges };
        }

        private StorageRangeMessage FulfillStorageRangeMessage(GetStorageRangeMessage getStorageRangeMessage, CancellationToken cancellationToken)
        {
            StorageRange? storageRange = getStorageRangeMessage.StorageRange;
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>? ranges, IByteArrayList? proofs) = SyncServer.GetStorageRanges(storageRange.RootHash, storageRange.Accounts,
                storageRange.StartingHash, storageRange.LimitHash, getStorageRangeMessage.ResponseBytes, cancellationToken);
            return new StorageRangeMessage { Proofs = proofs, Slots = ranges };
        }

        private ByteCodesMessage FulfillByteCodesMessage(GetByteCodesMessage getByteCodesMessage, CancellationToken cancellationToken)
        {
            IByteArrayList byteCodes = SyncServer.GetByteCodes(getByteCodesMessage.Hashes, getByteCodesMessage.Bytes, cancellationToken);
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

        public async Task<IByteArrayList> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
        {
            ByteCodesMessage response = await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
                SendRequest(new GetByteCodesMessage
                {
                    Hashes = codeHashes.ToPooledList(),
                    Bytes = bytesLimit,
                }, _getByteCodesRequests, token));

            return response.Codes;
        }

        public async Task<IByteArrayList> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
        {
            RlpPathGroupList groups = GetPathGroups(request);

            return await GetTrieNodes(request.RootHash, groups, token);
        }

        public async Task<IByteArrayList> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
        {
            return await GetTrieNodes(request.RootHash, request.AccountAndStoragePaths, token);
        }

        private async Task<IByteArrayList> GetTrieNodes(Hash256 rootHash, IOwnedReadOnlyList<PathGroup> groups, CancellationToken token)
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

        public static RlpPathGroupList GetPathGroups(AccountsToRefreshRequest request)
        {
            using DeferredRlpItemList.Builder builder = new();
            DeferredRlpItemList.Builder.Writer rootWriter = builder.BeginRootContainer();
            for (int i = 0; i < request.Paths.Count; i++)
            {
                AccountWithStorageStartingHash path = request.Paths[i];
                using DeferredRlpItemList.Builder.Writer groupWriter = rootWriter.BeginContainer();
                groupWriter.WriteValue(path.PathAndAccount.Path.Bytes);
                groupWriter.WriteValue(_emptyBytes);
            }
            rootWriter.Dispose();

            return new RlpPathGroupList(builder.ToRlpItemList());
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
