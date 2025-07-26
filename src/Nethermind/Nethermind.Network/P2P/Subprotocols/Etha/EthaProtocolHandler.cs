// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Etha.Messages;
using Nethermind.Network.P2P.Utils;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;

using MessageSizeEstimator = Nethermind.Network.P2P.Subprotocols.Eth.V62.MessageSizeEstimator;
using BlockBodiesMessageV62 = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.BlockBodiesMessage;
using GetBlockBodiesMessageV62 = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.GetBlockBodiesMessage;

using ReceiptsMessageV63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.ReceiptsMessage;
using GetReceiptsMessageV63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages.GetReceiptsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Etha;

public class EthaProtocolHandler : ZeroProtocolHandlerBase
{
    private const ulong SPAN_SIZE = 106_496;
    private const ulong RANGE_SIZE = 1_064_960;

    private readonly ISyncServer _syncServer;
    private readonly BackgroundTaskSchedulerWrapper _backgroundTaskScheduler;
    protected readonly ForkInfo _forkInfo;
    private readonly uint _localBitmask;
    private uint _peerBitmask;
    private bool _statusReceived;

    private readonly MessageDictionary<GetBlockBodiesMessage, (OwnedBlockBodies, long)> _bodiesRequests66;
    private readonly MessageDictionary<GetReceiptsMessage, (IOwnedReadOnlyList<TxReceipt[]>, long)> _receiptsRequests66;

    public override string Name => "etha1";
    protected override TimeSpan InitTimeout => Timeouts.Eth62Status;
    public override byte ProtocolVersion => 1;
    public override string ProtocolCode => Protocol.Etha;
    public override int MessageIdSpaceSize => 5;
    public static readonly ulong SoftOutgoingMessageSizeLimit = (ulong)2.MB();

    public EthaProtocolHandler(ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager statsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ForkInfo forkInfo,
        uint localBitmask,
        ILogManager logManager)
        : base(session, statsManager, serializer, logManager)
    {
        _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
        _backgroundTaskScheduler = new BackgroundTaskSchedulerWrapper(this, backgroundTaskScheduler ?? throw new ArgumentNullException(nameof(backgroundTaskScheduler)));
        _forkInfo = forkInfo ?? throw new ArgumentNullException(nameof(forkInfo));

        _localBitmask = localBitmask;
        _bodiesRequests66 = new MessageDictionary<GetBlockBodiesMessage, (OwnedBlockBodies, long)>(Send);
        _receiptsRequests66 = new MessageDictionary<GetReceiptsMessage, (IOwnedReadOnlyList<TxReceipt[]>, long)>(Send);
    }

    public override void Init()
    {
        if (Logger.IsTrace) Logger.Trace($"{Name} subprotocol initializing with  Node"); // @DEV Fix this

        if (_syncServer.Head is null)
        {
            throw new InvalidOperationException($"Cannot initialize {Name} without the head block set");
        }

        BlockHeader head = _syncServer.Head;
        NotifyOfStatus(head);

        CheckProtocolInitTimeout().ContinueWith(x =>
        {
            if (x.IsFaulted && Logger.IsError)
            {
                Logger.Error($"Error during {Name} handler timeout logic", x.Exception);
            }
        });
    }

    public override void Dispose()
    {
        ProtocolInitialized = null;
    }
    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
    {
        Dispose();
    }

    public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
    public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
    {
        add { }
        remove { }
    }

    public override void HandleMessage(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;

        switch (message.PacketType)
        {
            case EthaMessageCode.Status:
                StatusMessage statusMsg = Deserialize<StatusMessage>(message.Content);
                ReportIn(statusMsg, size);
                Handle(statusMsg);
                break;
            case EthaMessageCode.GetBlockBodies:
                GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                ReportIn(getBodiesMsg, size);
                _backgroundTaskScheduler.ScheduleSyncServe(getBodiesMsg, this.Handle);
                break;
            case EthaMessageCode.BlockBodies:
                BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                ReportIn(bodiesMsg, size);
                Handle(bodiesMsg, size);
                break;
            case EthaMessageCode.GetReceipts:
                GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                ReportIn(getReceiptsMessage, size);
                _backgroundTaskScheduler.ScheduleSyncServe(getReceiptsMessage, this.Handle);
                break;
            case EthaMessageCode.Receipts:
                ReceiptsMessage69 receiptsMessage = Deserialize<ReceiptsMessage69>(message.Content);
                ReportIn(receiptsMessage, size);
                Handle(receiptsMessage, size);
                break;
            default:
                if (Logger.IsDebug)
                {
                    Logger.Debug($"EthaProtocolHandler: Unknown packet type {message.PacketType} received in Etha protocol handler.");
                }
                break;
        }
    }

    private void Handle(StatusMessage status)
    {
        if (_statusReceived)
        {
            throw new SubprotocolException("StatusMessage has already been received in the past");
        }

        ValidateBitmask(status.BlockBitmask);
        _peerBitmask = status.BlockBitmask;
        _statusReceived = true;

        if (Logger.IsDebug)
        {
            Logger.Debug($"Etha status received from {Session.RemoteNodeId}. " +
                       $"Peer bitmask: 0x{_peerBitmask:X}, Local bitmask: 0x{_localBitmask:X}");
        }

        ReceivedProtocolInitMsg(status);
        ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));

    }
    private async Task<BlockBodiesMessage> Handle(GetBlockBodiesMessage getBlockBodies, CancellationToken cancellationToken)
    {
        using var message = getBlockBodies;
        BlockBodiesMessageV62 ethBlockBodiesMessage = await FulfillBlockBodiesRequest(message.EthMessage, cancellationToken);
        return new BlockBodiesMessage(message.RequestId, ethBlockBodiesMessage);
    }
    private void Handle(BlockBodiesMessage blockBodiesMessage, long size)
    {
        _bodiesRequests66.Handle(blockBodiesMessage.RequestId, (blockBodiesMessage.EthMessage.Bodies, size), size);
    }
    private async Task<ReceiptsMessage69> Handle(GetReceiptsMessage getReceiptsMessage, CancellationToken cancellationToken)
    {
        using var message = getReceiptsMessage;
        ReceiptsMessageV63 receiptsMessage = await FulfillReceiptsRequest(message.EthMessage, cancellationToken);
        return new(message.RequestId, receiptsMessage);
    }
    protected void Handle(ReceiptsMessage69 msg, long size)
    {
        _receiptsRequests66.Handle(msg.RequestId, (msg.EthMessage.TxReceipts, size), size);
    }

    protected Task<BlockBodiesMessageV62> FulfillBlockBodiesRequest(GetBlockBodiesMessageV62 getBlockBodiesMessage, CancellationToken cancellationToken)
    {
        IReadOnlyList<Hash256> hashes = getBlockBodiesMessage.BlockHashes;
        using ArrayPoolList<Block> blocks = new(hashes.Count);

        ulong sizeEstimate = 0;
        for (int i = 0; i < hashes.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            Block? block = _syncServer.Find(hashes[i]);

            if (block != null && HasSpan((ulong)block.Number))
            {
                blocks.Add(block);
                sizeEstimate += MessageSizeEstimator.EstimateSize(block);

                if (sizeEstimate > SoftOutgoingMessageSizeLimit)
                {
                    if (Logger.IsDebug)
                    {
                        Logger.Debug($"BlockBodies response size limit reached: {sizeEstimate} bytes");
                    }
                    break;
                }
            }
            else
            {
                blocks.Add(null);
            }
        }

        return Task.FromResult(new BlockBodiesMessageV62(blocks));
    }
    protected Task<ReceiptsMessageV63> FulfillReceiptsRequest(GetReceiptsMessageV63 getReceiptsMessage, CancellationToken cancellationToken)
    {
        ArrayPoolList<TxReceipt[]> txReceipts = new(getReceiptsMessage.Hashes.Count);

        ulong sizeEstimate = 0;
        for (int i = 0; i < getReceiptsMessage.Hashes.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            TxReceipt[] fetchedReceipts = _syncServer.GetReceipts(getReceiptsMessage.Hashes[i]);

            if (fetchedReceipts[0] != null && HasSpan((ulong)fetchedReceipts[0].BlockNumber))
            {
                txReceipts.Add(fetchedReceipts);

                for (int j = 0; j < txReceipts[i].Length; j++)
                {
                    sizeEstimate += MessageSizeEstimator.EstimateSize(txReceipts[i][j]);
                }

                if (sizeEstimate > SoftOutgoingMessageSizeLimit)
                {
                    if (Logger.IsDebug)
                    {
                        Logger.Debug($"Receipts response size limit reached: {sizeEstimate} bytes");
                    }
                    break;
                }
            }
            else
            {
                txReceipts.Add(null);
            }
        }

        return Task.FromResult(new ReceiptsMessageV63(txReceipts));
    }

    protected void NotifyOfStatus(BlockHeader head)
    {
        StatusMessage statusMessage = new()
        {
            ProtocolVersion = ProtocolVersion,
            NetworkId = _syncServer.NetworkId,
            GenesisHash = _syncServer.Genesis.Hash!,
            ForkId = _forkInfo.GetForkId(head.Number, head.Timestamp),
            EarliestBlock = 0,
            LatestBlock = head.Number,
            LatestBlockHash = head.Hash!,
            BlockBitmask = _localBitmask
        };

        Send(statusMessage);
    }

    private static void ValidateBitmask(uint bitmask)
    {
        // 10-bit bitmask, so max value is 0x3FF (1023)
        if (bitmask > 0x3FF)
        {
            throw new ArgumentException($"Invalid bitmask: 0x{bitmask:X}. Must be <= 0x3FF (10 bits)", nameof(bitmask));
        }
        return;
    }

    public bool HasSpan(ulong blockNumber)
    {
        int spanIndex = GetSpanIndex(blockNumber);
        return (_localBitmask & (1u << spanIndex)) != 0;
    }
    public static int GetSpanIndex(ulong blockNumber)
    {
        ulong rangeOffset = blockNumber % RANGE_SIZE;
        return (int)(rangeOffset / SPAN_SIZE);
    }
}