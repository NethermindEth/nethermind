// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Stats.SyncLimits;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using MemoryAllowance = Nethermind.TxPool.MemoryAllowance;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public abstract class SyncPeerProtocolHandlerBase : ZeroProtocolHandlerBase, ISyncPeer
    {
        protected const int MaxReceiptsLookups = 2 * NethermindSyncLimits.MaxReceiptFetch;
        private static readonly int MaxRequestIdLength = Rlp.LengthOf(ulong.MaxValue);

        internal static ulong SoftOutgoingMessageSizeLimit = 2UL.MiB;
        internal static ulong HardOutgoingReceiptsMessageSizeLimit = 10UL.MiB;
        internal static ulong HardOutgoingBodiesMessageSizeLimit = 15UL.MiB;
        // The size limits can end a response early, so blocks are read ahead a window at a time, not all at once.
        protected const int ServingPrefetchWindow = 16;
        public Node Node => Session.Node;
        public string? ClientId => Node.ClientId;
        public virtual UInt256? TotalDifficulty { get; set; } = UInt256.Zero; // for compatibility with old code, which relies on 0 being the default value
        public PublicKey Id => Node.Id;
        string ITxPoolPeer.Enode => Node.ToString();

        public virtual bool IncludeInTxPool => true;
        protected ISyncServer SyncServer { get; }

        private HeadClaim _head = new(null, 0);

        private sealed record HeadClaim(Hash256? Hash, ulong Number);

        public ulong HeadNumber
        {
            get => Volatile.Read(ref _head).Number;
            set
            {
                HeadClaim previous;
                do
                {
                    previous = Volatile.Read(ref _head);
                } while (!ReferenceEquals(Interlocked.CompareExchange(ref _head, new(previous.Hash, value), previous), previous));
            }
        }

        public Hash256 HeadHash
        {
            get => Volatile.Read(ref _head).Hash!;
            set
            {
                HeadClaim previous;
                do
                {
                    previous = Volatile.Read(ref _head);
                } while (!ReferenceEquals(Interlocked.CompareExchange(ref _head, new(value, previous.Number), previous), previous));
            }
        }

        /// <inheritdoc />
        public void SetHead(Hash256 hash, ulong number) => Volatile.Write(ref _head, new(hash, number));

        /// <inheritdoc />
        public bool TrySetHead(Hash256 expectedHash, ulong expectedNumber, Hash256 hash, ulong number)
        {
            HeadClaim previous = Volatile.Read(ref _head);
            return previous.Hash == expectedHash && previous.Number == expectedNumber
                && ReferenceEquals(Interlocked.CompareExchange(ref _head, new(hash, number), previous), previous);
        }

        // this means that we know what the number, hash, and total diff of the head block is
        public bool IsInitialized { get; set; }
        public override string ToString() => $"[Peer|{Name}|{HeadNumber,8}|{Node:a}|{Session?.Direction,4}]";

        protected Hash256 _remoteHeadBlockHash;
        protected readonly ITimestamper _timestamper;

        protected readonly MessageQueue<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader?>> _headersRequests;
        protected readonly MessageQueue<GetBlockBodiesMessage, (OwnedBlockBodies, long)> _bodiesRequests;

        private TransactionHashCache.PeerCache? _notifiedTransactions;
        private object? _notifiedTransactionsLock;
        private protected TransactionHashCache.PeerCache NotifiedTransactions =>
            LazyInitializer.EnsureInitialized(ref _notifiedTransactions, ref _notifiedTransactionsLock, static () => SharedTransactionHashes.Cache.CreatePeerCache());

        private static class SharedTransactionHashes
        {
            // Allow for different inbound histories across peers as well as the current pool.
            // TxPool sets MemPoolSize before peers start; this process-wide capacity is fixed on first use.
            internal static readonly TransactionHashCache Cache = new(4 * MemoryAllowance.MemPoolSize);
        }

        protected SyncPeerProtocolHandlerBase(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager statsManager,
            ISyncServer syncServer,
            IBackgroundTaskScheduler backgroundTaskScheduler,
            ILogManager logManager,
            TimeProvider? timeProvider = null) : base(session, statsManager, serializer, backgroundTaskScheduler, logManager, timeProvider)
        {
            SyncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _timestamper = Timestamper.Default;
            _headersRequests = new MessageQueue<GetBlockHeadersMessage, IOwnedReadOnlyList<BlockHeader>>(this);
            _bodiesRequests = new MessageQueue<GetBlockBodiesMessage, (OwnedBlockBodies, long)>(this);
        }

        public override void RegisterWith(ISession session, IProtocolRegistrar registrar)
        {
            SetProtocolRegistrar(registrar);
            registrar.Register(session, this);
        }

        public void Disconnect(DisconnectReason reason, string details)
        {
            if (Logger.IsTrace) Logger.Trace($"Disconnecting {Node?.ToString("c"):hide} because of the {details:hide}");
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

        async Task<IOwnedReadOnlyList<BlockHeader>> ISyncPeer.GetBlockHeaders(ulong number, int maxBlocks, int skip, CancellationToken token)
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
                Logger.Trace($"Sending headers request to {Session.Node?.ToString("c"):hide}:");
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

        void ISyncPeer.NotifyUsefulSyncResponse()
        {
            if (Session is Session session) session.MarkUsefulSyncResponse();
        }

        async Task<BlockHeader?> ISyncPeer.GetHeadBlockHeader(Hash256? hash, CancellationToken token)
        {
            Hash256? requestedHash = hash ?? _remoteHeadBlockHash;
            if (requestedHash is null) return null;

            GetBlockHeadersMessage msg = new();
            msg.StartBlockHash = requestedHash;
            msg.MaxHeaders = 1;
            msg.Reverse = 0;
            msg.Skip = 0;

            using IOwnedReadOnlyList<BlockHeader> headers = await SendRequest(msg, token);
            ReadOnlySpan<BlockHeader> headersSpan = headers.AsSpan();

            // A peer without the block answers with an empty list, or with an item that decodes to a null header.
            BlockHeader? header = headersSpan.Length == 0 ? null : headersSpan[0];
            if (header is null) return null;

            if (header.Hash != requestedHash)
            {
                Disconnect(DisconnectReason.UnexpectedHeaderHash, "header hash inconsistent with request");
                return null;
            }

            if (Session is Session session && HeaderValidator.ValidateHash(header) && SyncServer.FindHeader(requestedHash)?.Hash == requestedHash)
                session.MarkUsefulSyncResponse();
            return header;
        }

        Task<IOwnedReadOnlyList<BlockHeader>?> ISyncPeer.GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token) =>
            RequestBlockHeaders(startHash, maxBlocks, skip, reverse: false, token);

        Task<IOwnedReadOnlyList<BlockHeader>?> ISyncPeer.GetAncestorHeaders(Hash256 startHash, int maxBlocks, CancellationToken token) =>
            RequestBlockHeaders(startHash, maxBlocks, 0, reverse: true, token);

        Task<IOwnedReadOnlyList<BlockHeader>?> ISyncPeer.GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, bool reverse, CancellationToken token) =>
            RequestBlockHeaders(startHash, maxBlocks, skip, reverse, token);

        private async Task<IOwnedReadOnlyList<BlockHeader>?> RequestBlockHeaders(Hash256 startHash, int maxBlocks, int skip, bool reverse, CancellationToken token)
        {
            if (maxBlocks == 0)
            {
                return ArrayPoolList<BlockHeader>.Empty();
            }

            GetBlockHeadersMessage msg = new();
            msg.StartBlockHash = startHash;
            msg.MaxHeaders = maxBlocks;
            msg.Reverse = (byte)(reverse ? 1 : 0);
            msg.Skip = skip;

            return await SendRequest(msg, token);
        }

        public virtual Task<IOwnedReadOnlyList<TxReceipt[]>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token) => throw new NotSupportedException("Fast sync not supported by eth62 protocol");

        public virtual Task<IOwnedReadOnlyList<TxReceipt[]>> GetReceipts(IReadOnlyList<Hash256> blockHashes, ReadOnlyMemory<int> expectedReceiptCounts, CancellationToken token) =>
            GetReceipts(blockHashes, token);

        public virtual Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) => throw new NotSupportedException("Fast sync not supported by eth62 protocol");

        public abstract void NotifyOfNewBlock(Block block, SendBlockMode mode);

        private bool ShouldNotifyTransaction(Transaction tx, bool sendFullTx)
            => sendFullTx || (tx.Hash is not null && ShouldNotifyTransactionCore(tx));

        protected virtual bool ShouldNotifyTransactionCore(Transaction tx)
            => NotifiedTransactions.Set(tx.Hash!.ValueHash256);

        public void SendNewTransaction(Transaction tx)
        {
            if (ShouldNotifyTransaction(tx, sendFullTx: false))
            {
                SendNewTransactionCore(tx);
            }
        }

        protected virtual void SendNewTransactionCore(Transaction tx)
        {
            if (!tx.CarriesBlobs) //additional protection from sending full tx with blob
            {
                SendMessage(new ArrayPoolList<Transaction>(1) { tx });
            }
        }

        public void SendNewTransactions(ReadOnlySpan<Transaction> txs, bool sendFullTx = false)
        {
            using ArrayPoolListRef<Transaction> txsToSend = new(txs.Length);
            foreach (Transaction tx in txs)
            {
                if (ShouldNotifyTransaction(tx, sendFullTx))
                {
                    txsToSend.Add(tx);
                }
            }

            if (txsToSend.Count > 0)
            {
                SendNewTransactionsCore(txsToSend.AsSpan(), sendFullTx);
            }
        }

        protected virtual void SendNewTransactionsCore(ReadOnlySpan<Transaction> txs, bool sendFullTx)
        {
            int packetSizeLeft = TransactionsMessage.MaxPacketSize;
            ArrayPoolList<Transaction> txsToSend = [with(1024)];

            foreach (Transaction tx in txs)
            {
                int txSize = tx.GetLength();

                if (txSize > packetSizeLeft && txsToSend.Count > 0)
                {
                    SendMessage(txsToSend);
                    txsToSend = [with(1024)];
                    packetSizeLeft = TransactionsMessage.MaxPacketSize;
                }

                if (tx.Hash is not null && !tx.CarriesBlobs) //additional protection from sending full tx with blob
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
                Logger.Trace($"Received headers request from {Session.Node?.ToString("c"):hide}:");
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
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockHeaders to {Node?.ToString("c"):hide} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");

            return resp;
        }

        protected Task<BlockHeadersMessage> FulfillBlockHeadersRequest(GetBlockHeadersMessage msg, CancellationToken cancellationToken)
        {
            if (msg.MaxHeaders > 1024)
            {
                throw new InvalidPeerRequestException("Incoming headers request for more than 1024 headers", DisconnectReason.EthSyncException);
            }

            int maxHeaders = (int)msg.MaxHeaders;
            int skip = (int)msg.Skip;
            if ((ulong)msg.Skip > int.MaxValue)
            {
                // A skip above int.MaxValue is not served: only the first header is, which is also what geth returns
                // while no block lies that far away. Narrowed to an int, such a skip would be negative: a step of zero
                // repeats the first header, and a wrapped one walks a forward request back.
                maxHeaders = Math.Min(maxHeaders, 1);
                skip = 0;
            }

            Hash256 startingHash = msg.StartBlockHash;
            startingHash ??= SyncServer.FindHash(msg.StartBlockNumber);

            IOwnedReadOnlyList<byte> headers =
                startingHash is null || cancellationToken.IsCancellationRequested
                    ? ArrayPoolList<byte>.Empty()
                    : SyncServer.FindHeadersRlp(startingHash, maxHeaders, skip, msg.Reverse == 1);

            return Task.FromResult(new BlockHeadersMessage { EncodedHeaders = headers });
        }

        protected async Task<BlockBodiesMessage> Handle(GetBlockBodiesMessage request, CancellationToken cancellationToken)
        {
            using GetBlockBodiesMessage message = request;
            if (Logger.IsTrace) Logger.Trace($"Received bodies request of length {message.BlockHashes.Count} from {Session.Node?.ToString("c"):hide}:");

            long startTime = Stopwatch.GetTimestamp();

            Interlocked.Increment(ref Counter);
            BlockBodiesMessage resp = await FulfillBlockBodiesRequest(message, cancellationToken);
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} BlockBodies to {Node?.ToString("c"):hide} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            return resp;
        }

        protected Task<BlockBodiesMessage> FulfillBlockBodiesRequest(GetBlockBodiesMessage getBlockBodiesMessage, CancellationToken cancellationToken)
        {
            IReadOnlyList<Hash256> hashes = getBlockBodiesMessage.BlockHashes;
            using ArrayPoolList<MemoryManager<byte>> owners = new(hashes.Count);
            using ArrayPoolList<ReadOnlyMemory<byte>> blocks = new(hashes.Count);
            try
            {
                ulong sizeEstimate = 0;
                for (int i = 0; i < hashes.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    int remaining = hashes.Count - i;
                    if (i % ServingPrefetchWindow == 0 && remaining > 1) SyncServer.PrefetchBlocks(hashes, i, Math.Min(ServingPrefetchWindow, remaining));

                    MemoryManager<byte>? block = SyncServer.FindBlockRlp(hashes[i]);
                    if (block is null)
                    {
                        continue;
                    }

                    owners.Add(block);
                    ulong blockSize = (ulong)block.Memory.Length;

                    // Cap the message size; return the prefix (bodies match request hashes positionally).
                    if (sizeEstimate + blockSize > HardOutgoingBodiesMessageSizeLimit)
                    {
                        break;
                    }

                    blocks.Add(block.Memory);
                    sizeEstimate += blockSize;

                    // Soft limit keeps the common-case response small.
                    if (sizeEstimate > SoftOutgoingMessageSizeLimit)
                    {
                        break;
                    }
                }

                return Task.FromResult(new BlockBodiesMessage { Bodies = OwnedBlockBodies.FromEncodedBlocks(blocks) });
            }
            finally
            {
                foreach (IMemoryOwner<byte> owner in owners) owner.Dispose();
            }
        }

        protected void Handle(BlockHeadersMessage message, long size) => _headersRequests.Handle(message.BlockHeaders, size);

        protected void HandleBodies(BlockBodiesMessage blockBodiesMessage, long size) => _bodiesRequests.Handle((blockBodiesMessage.Bodies, size), size);

        protected async Task<ReceiptsMessage> Handle(GetReceiptsMessage msg, CancellationToken cancellationToken)
        {
            using GetReceiptsMessage message = msg;
            long startTime = Stopwatch.GetTimestamp();
            ReceiptsMessage resp = await FulfillReceiptsRequest(message, cancellationToken);
            if (Logger.IsTrace) Logger.Trace($"OUT {Counter:D5} Receipts to {Node?.ToString("c"):hide} in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");

            return resp;
        }

        /// <summary>Whether the receipts message of this protocol carries the bloom of each receipt.</summary>
        /// <remarks>
        /// Set by the message format, not by <see cref="ProtocolVersion"/>: XDC protocols have versions above eth/69
        /// but use the eth/63 receipts message.
        /// </remarks>
        protected virtual bool ReceiptsWithBloom => true;

        protected Task<ReceiptsMessage> FulfillReceiptsRequest(GetReceiptsMessage getReceiptsMessage, CancellationToken cancellationToken)
        {
            ReadOnlySpan<Hash256> hashes = getReceiptsMessage.Hashes.AsSpan();
            hashes = hashes[..Math.Min(hashes.Length, MaxReceiptsLookups)];
            bool withBloom = ReceiptsWithBloom;
            // Before eth/69 the message encodes receipts with the registered decoder, which a plugin can replace.
            // Stored receipts are copied without this decoder, so such a plugin must also decorate IReceiptFinder.
            IRlpDecoder<TxReceipt> decoder = withBloom ? Rlp.GetDecoderOrThrow<TxReceipt>() : ReceiptDecoder69;
            ArrayPoolList<byte> buffer = [with(GetReceiptsBufferCapacity(hashes.Length))];
            using ArrayPoolList<Range> blocks = new(hashes.Length);
            try
            {
                for (int i = 0; i < hashes.Length; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    Hash256 blockHash = hashes[i];
                    int remaining = hashes.Length - i;
                    if (i % ServingPrefetchWindow == 0 && remaining > 1) SyncServer.PrefetchReceipts(getReceiptsMessage.Hashes, i, Math.Min(ServingPrefetchWindow, remaining));

                    int start = buffer.Count;
                    if (!SyncServer.TryAppendReceipts(blockHash, decoder, withBloom, buffer)) break;

                    // Cap the message size; return the prefix (receipts match request hashes positionally).
                    if (GetMaxReceiptsResponseSize((ulong)buffer.Count) > HardOutgoingReceiptsMessageSizeLimit)
                    {
                        // An empty prefix means the block alone does not fit, so this peer can never get it from us.
                        if (blocks.Count == 0 && Logger.IsDebug) Logger.Debug($"Cannot serve receipts of {blockHash} to {Node?.ToString("c"):hide}: {buffer.Count - start} bytes exceeds the {HardOutgoingReceiptsMessageSizeLimit} bytes limit.");
                        buffer.Truncate(start);
                        break;
                    }

                    blocks.Add(new Range(start, buffer.Count));

                    // Soft limit keeps the common-case response small.
                    if ((ulong)buffer.Count > SoftOutgoingMessageSizeLimit) break;
                }

                return Task.FromResult(new ReceiptsMessage(NetworkBlockReceipts.FromBuffer(buffer, blocks.AsSpan().ToArray(), withBloom, withBloom ? DecodeReceipts : DecodeReceipts69)));
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        private static readonly RefRlpReaderFunc<TxReceipt[]> DecodeReceipts = static (ref RlpReader reader) =>
            reader.DecodeNonNullArray(static (ref RlpReader receipt) => Rlp.GetDecoderOrThrow<TxReceipt>().Decode(ref receipt)!);

        private static readonly ReceiptMessageDecoder69 ReceiptDecoder69 = new();

        private static readonly RefRlpReaderFunc<TxReceipt[]> DecodeReceipts69 = static (ref RlpReader reader) =>
            reader.DecodeNonNullArray(static (ref RlpReader receipt) => ReceiptDecoder69.Decode(ref receipt)!);

        /// <summary>The initial capacity of the buffer that the receipts of <paramref name="blocks"/> blocks are served from.</summary>
        /// <remarks>
        /// Sized up front, so the buffer does not double as it fills and copy the response at each step. A response stops
        /// after the block that crosses the soft limit, so twice the limit holds it unless that block is larger than the limit.
        /// </remarks>
        private protected static int GetReceiptsBufferCapacity(int blocks) =>
            (int)Math.Min((ulong)blocks * ReceiptsBufferBlockEstimate, 2 * SoftOutgoingMessageSizeLimit);

        private const int ReceiptsBufferBlockEstimate = 128 * 1024;

        internal static ulong GetMaxReceiptsResponseSize(ulong receiptsContentSize) =>
            receiptsContentSize > int.MaxValue / 2
                ? ulong.MaxValue
                : (ulong)Rlp.LengthOfSequence(MaxRequestIdLength + Rlp.LengthOfSequence((int)receiptsContentSize));


        #region Cleanup

        private int _isDisposed;

        protected abstract void OnDisposed();

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

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
        private Dictionary<string, object> ProtocolHandlers => _protocolHandlers ??= [];

        public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class => ProtocolHandlers[protocol] = protocolHandler;

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
