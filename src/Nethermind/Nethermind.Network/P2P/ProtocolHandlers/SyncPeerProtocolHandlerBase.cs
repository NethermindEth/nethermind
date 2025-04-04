// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using MemoryAllowance = Nethermind.TxPool.MemoryAllowance;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public abstract class SyncPeerProtocolHandlerBase : ZeroProtocolHandlerBase, ISyncPeer
    {
        public static readonly ulong SoftOutgoingMessageSizeLimit = (ulong)2.MB();
        public Node Node => Session?.Node;
        public string ClientId => Node?.ClientId;
        public UInt256 TotalDifficulty { get; set; }
        public PublicKey Id => Node.Id;
        string ITxPoolPeer.Enode => Node?.ToString();

        public virtual bool IncludeInTxPool => true;
        protected ISyncServer SyncServer { get; }
        protected BackgroundTaskSchedulerWrapper BackgroundTaskScheduler { get; }

        public long HeadNumber { get; set; }
        public Hash256 HeadHash { get; set; }

        // this means that we know what the number, hash, and total diff of the head block is
        public bool IsInitialized { get; set; }
        public override string ToString() => $"[Peer|{Name}|{HeadNumber,8}|{Node:a}|{Session?.Direction,4}]";

        protected Hash256 _remoteHeadBlockHash;
        protected readonly ITimestamper _timestamper;
        protected readonly TxDecoder _txDecoder;

        protected readonly MessageQueue<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader?>> _headersRequests;
        protected readonly MessageQueue<GetBlockBodiesMessage, (OwnedBlockBodies, long)> _bodiesRequests;

        protected ClockKeyCache<ValueHash256>? _notifiedTransactions;
        protected ClockKeyCache<ValueHash256> NotifiedTransactions => _notifiedTransactions ??= new(2 * MemoryAllowance.MemPoolSize);

        protected SyncPeerProtocolHandlerBase(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ILogManager logManager) : base(session, statsManager, serializer, logManager)
        {
            SyncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            BackgroundTaskScheduler = new BackgroundTaskSchedulerWrapper(this, backgroundTaskScheduler ?? throw new ArgumentNullException(nameof(BackgroundTaskScheduler)));
            _timestamper = Timestamper.Default;
            _txDecoder = TxDecoder.Instance;
            _headersRequests = new MessageQueue<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>>(Send);
            _bodiesRequests = new MessageQueue<GetBlockBodiesMessage, (OwnedBlockBodies, long)>(Send);
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            if (Logger.IsTrace) Logger.Trace($"Disconnecting {Node:c} because of the {details}");
            Session.InitiateDisconnect(reason, details);
        }

        async Task<OwnedBlockBodies> ISyncPeer.GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
        {
            if (blockHashes.Count == 0)
            {
                return new OwnedBlockBodies([]);
            }

            OwnedBlockBodies blocks = await _nodeStats.RunSizeAndLatencyRequestSizer<OwnedBlockBodies, Hash256, BlockBody?>(RequestType.Bodies, blockHashes, async clampedBlockHashes =>
                await SendRequest(new GetBlockBodiesMessage(clampedBlockHashes), token));

            return blocks;
        }

        protected virtual async Task<(OwnedBlockBodies, long)> SendRequest(GetBlockBodiesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Count}");
            }

            return await SendRequestGeneric(
                _bodiesRequests,
                message,
                TransferSpeedType.Bodies,
                static (message) => $"{nameof(GetBlockBodiesMessage)} with {message.BlockHashes.Count} block hashes",
                token);
        }

        async Task<IOwnedReadOnlyList<BlockHeader>> ISyncPeer.GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return ArrayPoolList<BlockHeader>.Empty();
            }

            GetBlockHeadersMessage msg = new();
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;
            msg.StartBlockNumber = number;

            IOwnedReadOnlyList<BlockHeader> headers = await SendRequest(msg, token);
            return headers;
        }

        protected virtual async Task<IOwnedReadOnlyList<BlockHeader>> SendRequest(GetBlockHeadersMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"Sending headers request to {Session.Node:c}:");
                Logger.Trace($"  Starting blockhash: {message.StartBlockHash}");
                Logger.Trace($"  Starting number: {message.StartBlockNumber}");
                Logger.Trace($"  Skip: {message.Skip}");
                Logger.Trace($"  Reverse: {message.Reverse}");
                Logger.Trace($"  Max headers: {message.MaxHeaders}");
            }

            return await SendRequestGeneric(
                _headersRequests,
                message,
                TransferSpeedType.Headers,
                static (message) => $"{nameof(GetBlockHeadersMessage)} with {message.MaxHeaders} max headers",
                token);
        }

        async Task<BlockHeader?> ISyncPeer.GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            GetBlockHeadersMessage msg = new();
            msg.StartBlockHash = hash ?? _remoteHeadBlockHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            using IOwnedReadOnlyList<BlockHeader> headers = await SendRequest(msg, token);
            return headers.Count > 0 ? headers[0] : null;
        }

        async Task<IOwnedReadOnlyList<BlockHeader>> ISyncPeer.GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return ArrayPoolList<BlockHeader>.Empty();
            }

            GetBlockHeadersMessage msg = new();
            msg.StartBlockHash = startHash;
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = 0;
            msg.Skip = skip;

            IOwnedReadOnlyList<BlockHeader> headers = await SendRequest(msg, token);
            return headers;
        }

        public virtual Task<IOwnedReadOnlyList<TxReceipt[]>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
        {
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public virtual Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
        {
            throw new NotSupportedException("Fast sync not supported by eth62 protocol");
        }

        public abstract void NotifyOfNewBlock(Block block, SendBlockMode mode);

        private bool ShouldNotifyTransaction(Hash256? hash) => hash is not null && NotifiedTransactions.Set(hash);

        public void SendNewTransaction(Transaction tx)
        {
            if (ShouldNotifyTransaction(tx.Hash))
            {
                SendNewTransactionCore(tx);
            }
        }

        protected virtual void SendNewTransactionCore(Transaction tx)
        {
            if (!tx.SupportsBlobs) //additional protection from sending full tx with blob
            {
                SendMessage(new ArrayPoolList<Transaction>(1) { tx });
            }
        }

        public void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx = false)
        {
            SendNewTransactionsCore(TxsToSendAndMarkAsNotified(txs, sendFullTx), sendFullTx);
        }

        private IEnumerable<Transaction> TxsToSendAndMarkAsNotified(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            foreach (Transaction tx in txs)
            {
                if (sendFullTx || ShouldNotifyTransaction(tx.Hash))
                {
                    yield return tx;
                }
            }
        }

        protected virtual void SendNewTransactionsCore(IEnumerable<Transaction> txs, bool sendFullTx)
        {
            int packetSizeLeft = TransactionsMessage.MaxPacketSize;
            ArrayPoolList<Transaction> txsToSend = new(1024);

            foreach (Transaction tx in txs)
            {
                int txSize = tx.GetLength();

                if (txSize > packetSizeLeft && txsToSend.Count > 0)
                {
                    SendMessage(txsToSend);
                    txsToSend = new(1024);
                    packetSizeLeft = TransactionsMessage.MaxPacketSize;
                }

                if (tx.Hash is not null && !tx.SupportsBlobs) //additional protection from sending full tx with blob
                {
                    txsToSend.Add(tx);
                    packetSizeLeft -= txSize;
                    TxPool.Metrics.PendingTransactionsSent++;
                }
            }

            if (txsToSend.Count > 0)
            {
                SendMessage(txsToSend);
            }
            else
            {
                txsToSend.Dispose();
            }
        }

        private void SendMessage(IOwnedReadOnlyList<Transaction> txsToSend)
        {
            TransactionsMessage msg = new(txsToSend);
            Send(msg);
        }

        protected async Task<BlockHeadersMessage> Handle(GetBlockHeadersMessage getBlockHeadersMessage, CancellationToken cancellationToken)
        {
            using GetBlockHeadersMessage message = getBlockHeadersMessage;
            long startTime = Stopwatch.GetTimestamp();
            if (Logger.IsTrace)
            {
                Logger.Trace($"Received headers request from {Session.Node:c}:");
                Logger.Trace($"  MaxHeaders: {message.MaxHeaders}");
                Logger.Trace($"  Reverse: {message.Reverse}");
                Logger.Trace($"  Skip: {message.Skip}");
                Logger.Trace($"  StartingBlockhash: {message.StartBlockHash}");
                Logger.Trace($"  StartingBlockNumber: {message.StartBlockNumber}");
            }

            // // to clearly state that this client is an ETH client and not ETC (and avoid disconnections on reversed sync)
            // // also to improve performance as this is the most common request
            // if (getBlockHeadersMessage.StartingBlockNumber == 1920000 && getBlockHeadersMessage.MaxHeaders == 1)
            // {
            //     // hardcoded response
            //     // Packet packet = new Packet(ProtocolCode, Eth62MessageCode.BlockHeaders, Bytes.FromHexString("f90210f9020da0a218e2c611f21232d857e3c8cecdcdf1f65f25a4477f98f6f47e4063807f2308a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794bcdfc35b86bedf72f0cda046a3c16829a2ef41d1a0c5e389416116e3696cce82ec4533cce33efccb24ce245ae9546a4b8f0d5e9a75a07701df8e07169452554d14aadd7bfa256d4a1d0355c1d174ab373e3e2d0a3743a026cf9d9422e9dd95aedc7914db690b92bab6902f5221d62694a2fa5d065f534bb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008638c3bf2616aa831d4c008347e7c08301482084578f7aa88d64616f2d686172642d666f726ba05b5acbf4bf305f948bd7be176047b20623e1417f75597341a059729165b9239788bede87201de42426"));
            //     // Session.DeliverMessage(packet);
            //     LazyInitializer.EnsureInitialized(ref _eth1920000HeaderMessage, () => Deserialize<BlockHeadersMessage>(Bytes.FromHexString("f90210f9020da0a218e2c611f21232d857e3c8cecdcdf1f65f25a4477f98f6f47e4063807f2308a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794bcdfc35b86bedf72f0cda046a3c16829a2ef41d1a0c5e389416116e3696cce82ec4533cce33efccb24ce245ae9546a4b8f0d5e9a75a07701df8e07169452554d14aadd7bfa256d4a1d0355c1d174ab373e3e2d0a3743a026cf9d9422e9dd95aedc7914db690b92bab6902f5221d62694a2fa5d065f534bb90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008638c3bf2616aa831d4c008347e7c08301482084578f7aa88d64616f2d686172642d666f726ba05b5acbf4bf305f948bd7be176047b20623e1417f75597341a059729165b9239788bede87201de42426")));
            //     Session.DeliverMessage(_eth1920000HeaderMessage);
            //
            //     if (Logger.IsTrace) Logger.Trace($"OUT hardcoded 1920000 BlockHeaders to {Node:c}");
            //     return;
            // }

            BlockHeadersMessage resp = await FulfillBlockHeadersRequest(message, cancellationToken);
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockHeaders to {Node:c} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");

            return resp;
        }

        protected Task<BlockHeadersMessage> FulfillBlockHeadersRequest(GetBlockHeadersMessage msg, CancellationToken cancellationToken)
        {
            if (msg.MaxHeaders > 1024)
            {
                throw new EthSyncException("Incoming headers request for more than 1024 headers");
            }

            Hash256 startingHash = msg.StartBlockHash;
            startingHash ??= SyncServer.FindHash(msg.StartBlockNumber);

            IOwnedReadOnlyList<BlockHeader> headers =
                startingHash is null
                    ? ArrayPoolList<BlockHeader>.Empty()
                    : SyncServer.FindHeaders(startingHash, (int)msg.MaxHeaders, (int)msg.Skip, msg.Reverse == 1);

            headers = FixHeadersForGeth(headers);

            return Task.FromResult(new BlockHeadersMessage(headers));
        }

        protected async Task<BlockBodiesMessage> Handle(GetBlockBodiesMessage request, CancellationToken cancellationToken)
        {
            using GetBlockBodiesMessage message = request;
            if (Logger.IsTrace)
            {
                Logger.Trace($"Received bodies request of length {message.BlockHashes.Count} from {Session.Node:c}:");
            }

            long startTime = Stopwatch.GetTimestamp();

            Interlocked.Increment(ref Counter);
            BlockBodiesMessage resp = await FulfillBlockBodiesRequest(message, cancellationToken);
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockBodies to {Node:c} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            return resp;
        }

        protected Task<BlockBodiesMessage> FulfillBlockBodiesRequest(GetBlockBodiesMessage getBlockBodiesMessage, CancellationToken cancellationToken)
        {
            IReadOnlyList<Hash256> hashes = getBlockBodiesMessage.BlockHashes;
            using ArrayPoolList<Block> blocks = new(hashes.Count);

            ulong sizeEstimate = 0;
            for (int i = 0; i < hashes.Count; i++)
            {
                Block block = SyncServer.Find(hashes[i]);
                blocks.Add(block);
                sizeEstimate += MessageSizeEstimator.EstimateSize(block);

                if (sizeEstimate > SoftOutgoingMessageSizeLimit || cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            return Task.FromResult(new BlockBodiesMessage(blocks));
        }

        protected void Handle(BlockHeadersMessage message, long size)
        {
            _headersRequests.Handle(message.BlockHeaders, size);
        }

        protected void HandleBodies(BlockBodiesMessage blockBodiesMessage, long size)
        {
            _bodiesRequests.Handle((blockBodiesMessage.Bodies, size), size);
        }

        protected async Task<ReceiptsMessage> Handle(GetReceiptsMessage msg, CancellationToken cancellationToken)
        {
            using var message = msg;
            if (message.Hashes.Count > 512)
            {
                throw new EthSyncException("Incoming receipts request for more than 512 blocks");
            }

            long startTime = Stopwatch.GetTimestamp();
            ReceiptsMessage resp = await FulfillReceiptsRequest(message, cancellationToken);
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} Receipts to {Node:c} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");

            return resp;
        }

        protected Task<ReceiptsMessage> FulfillReceiptsRequest(GetReceiptsMessage getReceiptsMessage, CancellationToken cancellationToken)
        {
            ArrayPoolList<TxReceipt[]> txReceipts = new(getReceiptsMessage.Hashes.Count);

            ulong sizeEstimate = 0;
            for (int i = 0; i < getReceiptsMessage.Hashes.Count; i++)
            {
                txReceipts.Add(SyncServer.GetReceipts(getReceiptsMessage.Hashes[i]));
                for (int j = 0; j < txReceipts[i].Length; j++)
                {
                    sizeEstimate += MessageSizeEstimator.EstimateSize(txReceipts[i][j]);
                }

                if (sizeEstimate > SoftOutgoingMessageSizeLimit || cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            return Task.FromResult(new ReceiptsMessage(txReceipts));
        }

        private static IOwnedReadOnlyList<BlockHeader> FixHeadersForGeth(IOwnedReadOnlyList<BlockHeader> headers)
        {
            int emptyBlocksAtTheEnd = 0;
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[headers.Count - 1 - i] is null)
                {
                    emptyBlocksAtTheEnd++;
                }
                else
                {
                    break;
                }
            }

            if (emptyBlocksAtTheEnd != 0)
            {
                int toTake = headers.Count - emptyBlocksAtTheEnd;
                if (headers is ArrayPoolList<BlockHeader> asArrayPoolList)
                {
                    asArrayPoolList.Truncate(toTake);
                    return headers;
                }

                ArrayPoolList<BlockHeader> newList = new ArrayPoolList<BlockHeader>(toTake, headers.Take(toTake));
                headers.Dispose();
                return newList;
            }

            return headers;
        }


        #region Cleanup

        private int _isDisposed;

        protected abstract void OnDisposed();

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                OnDisposed();

                try
                {
                    _headersRequests?.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    _bodiesRequests?.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        #endregion

        #region IPeerWithSatelliteProtocol

        private Dictionary<string, object>? _protocolHandlers;
        private Dictionary<string, object> ProtocolHandlers => _protocolHandlers ??= new Dictionary<string, object>();

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
        {
            ProtocolHandlers[protocol] = protocolHandler;
        }

        public bool TryGetSatelliteProtocol<T>(string protocol, out T? protocolHandler) where T : class
        {
            if (ProtocolHandlers.TryGetValue(protocol, out object handler))
            {
                protocolHandler = handler as T;
                return protocolHandler is not null;
            }

            protocolHandler = null;
            return false;
        }

        #endregion
    }
}
