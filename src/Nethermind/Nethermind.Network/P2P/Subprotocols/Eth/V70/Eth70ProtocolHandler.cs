// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-7975 - partial block receipt lists.
/// </summary>
public class Eth70ProtocolHandler : Eth69ProtocolHandler, IStaticProtocolInfo
{
    private static readonly ReceiptMessageDecoder69 ReceiptMessageDecoder = new();

    private readonly ISpecProvider _specProvider;
    private readonly MessageDictionary<GetReceiptsMessage70, ReceiptsMessage70> _receiptsRequests70;

    public Eth70ProtocolHandler(
        ISession session,
        IMessageSerializationService serializer,
        INodeStatsManager nodeStatsManager,
        ISyncServer syncServer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ITxPool txPool,
        IGossipPolicy gossipPolicy,
        IForkInfo forkInfo,
        ILogManager logManager,
        ITxPoolConfig txPoolConfig,
        ISpecProvider specProvider,
        ITxGossipPolicy? transactionsGossipPolicy = null)
        : base(session, serializer, nodeStatsManager, syncServer, backgroundTaskScheduler, txPool,
            gossipPolicy, forkInfo, logManager, txPoolConfig, specProvider, transactionsGossipPolicy)
    {
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _receiptsRequests70 = new MessageDictionary<GetReceiptsMessage70, ReceiptsMessage70>(Send);
    }

    public override string Name => "eth70";

    public new static byte Version => EthVersions.Eth70;
    public override byte ProtocolVersion => Version;

    protected override void HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;
        switch (message.PacketType)
        {
            case Eth70MessageCode.Receipts:
                ReceiptsMessage70 receiptsMessage = Deserialize<ReceiptsMessage70>(message.Content);
                ReportIn(receiptsMessage, size);
                Handle(receiptsMessage, size);
                break;
            case Eth70MessageCode.GetReceipts:
                HandleInBackground<GetReceiptsMessage70, ReceiptsMessage70>(message, Handle);
                break;
            default:
                base.HandleMessageCore(message);
                break;
        }
    }

    private void Handle(ReceiptsMessage70 msg, long size) => _receiptsRequests70.Handle(msg.RequestId, msg, size);

    internal ValueTask<ReceiptsMessage70> Handle(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        using GetReceiptsMessage70 message = getReceiptsMessage;
        ReceiptsResponse response = FulfillReceiptsRequest(message, cancellationToken);
        return ValueTask.FromResult(new ReceiptsMessage70(message.RequestId, response.TxReceipts, response.LastBlockIncomplete));
    }

    private ReceiptsResponse FulfillReceiptsRequest(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        ArrayPoolList<TxReceipt[]> txReceipts = new(getReceiptsMessage.Hashes.Count);
        bool lastBlockIncomplete = false;

        try
        {
            ulong responseReceiptsContentSize = 0;
            for (int blockIndex = 0; blockIndex < getReceiptsMessage.Hashes.Count; blockIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Hash256 blockHash = getReceiptsMessage.Hashes[blockIndex];
                if (SyncServer.FindHeader(blockHash) is null)
                {
                    break;
                }

                TxReceipt[] receipts = SyncServer.GetReceipts(blockHash);
                long requestedStartIndex = blockIndex == 0 ? getReceiptsMessage.FirstBlockReceiptIndex : 0;
                if (requestedStartIndex < 0 || requestedStartIndex > receipts.Length)
                {
                    throw new SubprotocolException($"Invalid firstBlockReceiptIndex {requestedStartIndex} for block receipts length {receipts.Length}");
                }

                int startIndex = (int)requestedStartIndex;

                if (receipts.Length == 0)
                {
                    ulong emptyBlockSize = GetBlockReceiptsSize(0);
                    ulong responseSize = GetEth70ReceiptsResponseSize(
                        responseReceiptsContentSize + emptyBlockSize,
                        getReceiptsMessage.RequestId);
                    if (responseSize > SoftOutgoingMessageSizeLimit && responseReceiptsContentSize > 0)
                    {
                        break;
                    }

                    if (startIndex != 0)
                    {
                        throw new SubprotocolException($"Invalid firstBlockReceiptIndex {startIndex} for empty receipts block");
                    }

                    txReceipts.Add([]);
                    responseReceiptsContentSize += emptyBlockSize;
                    continue;
                }

                if (startIndex >= receipts.Length)
                {
                    throw new SubprotocolException($"Invalid firstBlockReceiptIndex {startIndex} for block receipts length {receipts.Length}");
                }

                ulong remainingBlockSize = GetBlockReceiptsSize(receipts, startIndex);
                if (GetEth70ReceiptsResponseSize(
                    responseReceiptsContentSize + remainingBlockSize,
                    getReceiptsMessage.RequestId) <= SoftOutgoingMessageSizeLimit)
                {
                    txReceipts.Add(CopyReceipts(receipts, startIndex, receipts.Length - startIndex));
                    responseReceiptsContentSize += remainingBlockSize;
                    continue;
                }

                if (responseReceiptsContentSize > 0)
                {
                    break;
                }

                if (GetEth70ReceiptsResponseSize(remainingBlockSize, getReceiptsMessage.RequestId) <= HardOutgoingReceiptsMessageSizeLimit)
                {
                    txReceipts.Add(CopyReceipts(receipts, startIndex, receipts.Length - startIndex));
                    break;
                }

                ulong blockReceiptsContentSize = 0;
                int taken = 0;
                long lastBlockNumber = -1;
                RlpBehaviors behaviors = RlpBehaviors.None;
                for (int receiptIndex = startIndex; receiptIndex < receipts.Length; receiptIndex++)
                {
                    ulong receiptSize = GetReceiptSize(receipts[receiptIndex], ref lastBlockNumber, ref behaviors);
                    ulong nextBlockReceiptsContentSize = blockReceiptsContentSize + receiptSize;
                    ulong nextBlockSize = GetBlockReceiptsSize(nextBlockReceiptsContentSize);
                    if (GetEth70ReceiptsResponseSize(nextBlockSize, getReceiptsMessage.RequestId) > HardOutgoingReceiptsMessageSizeLimit)
                    {
                        break;
                    }

                    taken++;
                    blockReceiptsContentSize = nextBlockReceiptsContentSize;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (taken == 0)
                {
                    throw new SubprotocolException($"Single receipt exceeds hard eth/70 receipts response size limit ({HardOutgoingReceiptsMessageSizeLimit} bytes)");
                }

                lastBlockIncomplete = startIndex + taken < receipts.Length;
                txReceipts.Add(CopyReceipts(receipts, startIndex, taken));
                break;
            }

            return new ReceiptsResponse(txReceipts, lastBlockIncomplete);
        }
        catch
        {
            txReceipts.Dispose();
            throw;
        }
    }

    public override async Task<IOwnedReadOnlyList<TxReceipt[]>> GetReceipts(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        if (blockHashes.Count == 0)
        {
            return ArrayPoolList<TxReceipt[]>.Empty();
        }

        return await _nodeStats.RunSizeAndLatencyRequestSizer<IOwnedReadOnlyList<TxReceipt[]>, Hash256, TxReceipt[]>(
            RequestType.Receipts,
            blockHashes,
            async clampedHashes =>
            {
                using ArrayPoolList<Hash256> ownedHashes = clampedHashes.ToPooledList();
                return await SendGetReceiptsWithPaging(ownedHashes, token);
            });
    }

    private async Task<(IOwnedReadOnlyList<TxReceipt[]>, long)> SendGetReceiptsWithPaging(IOwnedReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        ArrayPoolList<TxReceipt[]> aggregated = new(blockHashes.Count);
        ArrayPoolList<TxReceipt>? partialReceipts = null;
        long partialReceiptsGas = 0;
        int blockIndex = 0;
        int firstBlockReceiptIndex = 0;
        ulong totalResponseSize = 0;

        using ArrayPoolList<long> expectedGasUsed = new(blockHashes.Count);

        for (int i = 0; i < blockHashes.Count; i++)
        {
            BlockHeader? header = SyncServer.FindHeader(blockHashes[i]);
            expectedGasUsed.Add(header?.GasUsed ?? 0);
        }

        try
        {
            while (blockIndex < blockHashes.Count)
            {
                using GetReceiptsMessage70 request = BuildRequest(blockHashes, blockIndex, firstBlockReceiptIndex);
                (ReceiptsMessage70 response, ulong size) = await SendRequest(request, token);

                using (response)
                {
                    totalResponseSize += size;

                    if (response.TxReceipts.Count == 0)
                    {
                        if (response.LastBlockIncomplete || firstBlockReceiptIndex > 0)
                        {
                            throw new SubprotocolException("Peer returned no progress for partial receipts");
                        }

                        break;
                    }

                    if (size > HardOutgoingReceiptsMessageSizeLimit)
                    {
                        throw new SubprotocolException($"Received eth/70 receipts response above hard limit ({size} bytes > {HardOutgoingReceiptsMessageSizeLimit} bytes)");
                    }

                    if (response.TxReceipts.Count > blockHashes.Count - blockIndex)
                    {
                        throw new SubprotocolException("Received more receipts than requested in eth/70 response");
                    }

                    if (response.LastBlockIncomplete && size < SoftOutgoingMessageSizeLimit / 4)
                    {
                        throw new SubprotocolException($"Received partial receipts response below minimum size ({size} bytes < {SoftOutgoingMessageSizeLimit / 4} bytes)");
                    }

                    IOwnedReadOnlyList<TxReceipt[]?> txReceipts = response.TxReceipts;

                    for (int i = 0; i < txReceipts.Count; i++)
                    {
                        bool isFirst = i == 0;
                        bool isLast = i == txReceipts.Count - 1;
                        TxReceipt[]? blockReceipts = txReceipts[i] ?? throw new SubprotocolException("Unexpected null receipt block payload");

                        if (isFirst && firstBlockReceiptIndex > 0)
                        {
                            if (partialReceipts is null)
                            {
                                throw new SubprotocolException("Unexpected receipts continuation without pending state");
                            }

                            partialReceiptsGas = partialReceipts[^1].GasUsedTotal;
                            partialReceipts.AddRange(blockReceipts);
                            ValidateBlockReceipts(blockReceipts, expectedGasUsed[blockIndex], firstBlockReceiptIndex, !response.LastBlockIncomplete || !isLast, partialReceiptsGas);

                            if (response.LastBlockIncomplete && isLast)
                            {
                                if (blockReceipts.Length == 0)
                                {
                                    throw new SubprotocolException("Peer returned no progress for partial receipts");
                                }

                                firstBlockReceiptIndex = partialReceipts.Count;
                            }
                            else
                            {
                                TxReceipt[] completed = partialReceipts.AsSpan().ToArray();
                                aggregated.Add(completed);
                                partialReceipts.Dispose();
                                partialReceipts = null;
                                blockIndex++;
                                firstBlockReceiptIndex = 0;
                                partialReceiptsGas = 0;
                            }

                            continue;
                        }

                        if (response.LastBlockIncomplete && isLast)
                        {
                            if (blockReceipts.Length == 0)
                            {
                                throw new SubprotocolException("Peer returned no progress for partial receipts");
                            }

                            ValidateBlockReceipts(blockReceipts, expectedGasUsed[blockIndex], firstBlockReceiptIndex, false, partialReceiptsGas);
                            partialReceipts = new ArrayPoolList<TxReceipt>(blockReceipts.Length + firstBlockReceiptIndex);
                            partialReceipts.AddRange(blockReceipts);
                            firstBlockReceiptIndex = partialReceipts.Count;

                            partialReceiptsGas = blockReceipts[^1].GasUsedTotal;

                            continue;
                        }

                        ValidateBlockReceipts(blockReceipts, expectedGasUsed[blockIndex], firstBlockReceiptIndex, true, partialReceiptsGas);
                        aggregated.Add(blockReceipts);
                        blockIndex++;
                        firstBlockReceiptIndex = 0;
                        partialReceiptsGas = 0;
                    }

                    if (!response.LastBlockIncomplete && blockIndex < blockHashes.Count)
                    {
                        break;
                    }
                }
            }

            return (aggregated, (long)totalResponseSize);
        }
        catch
        {
            aggregated.Dispose();
            partialReceipts?.Dispose();
            throw;
        }
    }

    private static TxReceipt[] CopyReceipts(TxReceipt[] receipts, int startIndex, int count)
    {
        TxReceipt[] copy = new TxReceipt[count];
        Array.Copy(receipts, startIndex, copy, 0, count);
        return copy;
    }

    private ulong GetBlockReceiptsSize(TxReceipt[] receipts, int startIndex)
    {
        ulong receiptsContentSize = 0;
        long lastBlockNumber = -1;
        RlpBehaviors behaviors = RlpBehaviors.None;
        for (int i = startIndex; i < receipts.Length; i++)
        {
            receiptsContentSize += GetReceiptSize(receipts[i], ref lastBlockNumber, ref behaviors);
        }

        return GetBlockReceiptsSize(receiptsContentSize);
    }

    private static ulong GetBlockReceiptsSize(ulong receiptsContentSize) =>
        GetRlpSequenceSize(receiptsContentSize);

    private static ulong GetEth70ReceiptsResponseSize(ulong receiptsContentSize, long requestId)
    {
        ulong receiptsMessageSize = GetRlpSequenceSize(receiptsContentSize);
        ulong responseContentSize =
            (ulong)Rlp.LengthOf(requestId) +
            (ulong)Rlp.LengthOf(1) +
            receiptsMessageSize;

        return GetRlpSequenceSize(responseContentSize);
    }

    private ulong GetReceiptSize(TxReceipt receipt, ref long lastBlockNumber, ref RlpBehaviors behaviors)
    {
        if (receipt.BlockNumber != lastBlockNumber)
        {
            lastBlockNumber = receipt.BlockNumber;
            IReceiptSpec receiptSpec = _specProvider.GetReceiptSpec(lastBlockNumber);
            behaviors = receiptSpec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
        }

        return (ulong)ReceiptMessageDecoder.GetLength(receipt, behaviors);
    }

    private static ulong GetRlpSequenceSize(ulong contentSize) =>
        contentSize > int.MaxValue
            ? ulong.MaxValue
            : (ulong)Rlp.LengthOfSequence((int)contentSize);

    private static GetReceiptsMessage70 BuildRequest(IOwnedReadOnlyList<Hash256> blockHashes, int startIndex, int firstReceiptIndex)
    {
        IOwnedReadOnlyList<Hash256> remainingHashes = blockHashes.Slice(startIndex, blockHashes.Count - startIndex);

        return new GetReceiptsMessage70(remainingHashes, firstReceiptIndex);
    }

    private async Task<(ReceiptsMessage70 response, ulong size)> SendRequest(GetReceiptsMessage70 message, CancellationToken token)
    {
        Request<GetReceiptsMessage70, ReceiptsMessage70> request = new(message);
        _receiptsRequests70.Send(request);

        ReceiptsMessage70 response = await HandleResponse(request, TransferSpeedType.Receipts, static _ => nameof(GetReceiptsMessage70), token);
        return (response, (ulong)request.ResponseSize);
    }

    private readonly struct ReceiptsResponse(IOwnedReadOnlyList<TxReceipt[]> txReceipts, bool lastBlockIncomplete)
    {
        public IOwnedReadOnlyList<TxReceipt[]> TxReceipts { get; } = txReceipts;

        public bool LastBlockIncomplete { get; } = lastBlockIncomplete;
    }

    private static void ValidateBlockReceipts(TxReceipt[] blockReceipts, long expectedGasUsed, int firstReceiptIndex, bool isCompleteSegment, long previousGasUsed)
    {
        if (blockReceipts is { Length: 0 })
        {
            ValidateEmptyReceiptsSegment(firstReceiptIndex, isCompleteSegment);
            return;
        }

        previousGasUsed = GetPreviousGasUsedForSegment(firstReceiptIndex, previousGasUsed);

        for (int i = 0; i < blockReceipts.Length; i++)
        {
            previousGasUsed = ValidateReceiptGas(blockReceipts[i], previousGasUsed);
        }

        ValidateSegmentGasAgainstHeader(previousGasUsed, expectedGasUsed, isCompleteSegment);
    }

    private static void ValidateEmptyReceiptsSegment(int firstReceiptIndex, bool isCompleteSegment)
    {
        if (firstReceiptIndex != 0 || !isCompleteSegment)
        {
            throw new SubprotocolException("Unexpected empty receipt block payload");
        }
    }

    private static long GetPreviousGasUsedForSegment(int firstReceiptIndex, long previousGasUsed)
    {
        long minimalPreviousGasUsed = checked((long)firstReceiptIndex * GasCostOf.Transaction);
        return Math.Max(previousGasUsed, minimalPreviousGasUsed);
    }

    private static long ValidateReceiptGas(TxReceipt receipt, long previousGasUsed)
    {
        long receiptGasUsed = GetReceiptGasUsed(receipt, previousGasUsed);
        ValidateReceiptGasCoversIntrinsicCost(receiptGasUsed);
        ValidateReceiptGasCoversLogs(receipt, receiptGasUsed);
        return receipt.GasUsedTotal;
    }

    private static long GetReceiptGasUsed(TxReceipt receipt, long previousGasUsed)
    {
        if (receipt.GasUsedTotal < previousGasUsed)
        {
            throw new SubprotocolException("Cumulative gas decreased within block receipts");
        }

        return receipt.GasUsedTotal - previousGasUsed;
    }

    private static void ValidateReceiptGasCoversIntrinsicCost(long receiptGasUsed)
    {
        if (GasCostOf.Transaction > receiptGasUsed)
        {
            throw new SubprotocolException("Intrinsic gas lower bound exceeds block gas used");
        }
    }

    private static void ValidateReceiptGasCoversLogs(TxReceipt receipt, long receiptGasUsed)
    {
        long logsGas = CalculateLogsGas(receipt);
        long gasAvailableForLogs = receiptGasUsed - GasCostOf.Transaction;
        if (logsGas > gasAvailableForLogs)
        {
            throw new SubprotocolException("Logs gas exceeds block gas used");
        }
    }

    private static void ValidateSegmentGasAgainstHeader(long actualGasUsed, long expectedGasUsed, bool isCompleteSegment)
    {
        if (expectedGasUsed > 0 && actualGasUsed > expectedGasUsed)
        {
            throw new SubprotocolException("Block gas used exceeds header value");
        }

        if (isCompleteSegment)
        {
            if (expectedGasUsed > 0 && actualGasUsed != expectedGasUsed)
            {
                throw new SubprotocolException("Block gas used mismatch between receipts and header");
            }
        }
    }

    private static long CalculateLogsGas(TxReceipt receipt)
    {
        if (receipt.Logs is null)
        {
            return 0;
        }

        long logsGas = 0;
        foreach (LogEntry log in receipt.Logs)
        {
            int topics = log.Topics?.Length ?? 0;
            int dataLength = log.Data?.Length ?? 0;
            logsGas = AddLogGas(logsGas, topics, dataLength);
        }

        return logsGas;
    }

    internal static long AddLogGas(long logsGas, int topics, int dataLength)
    {
        try
        {
            return checked(logsGas + GasCostOf.Log + (long)topics * GasCostOf.LogTopic + (long)dataLength * GasCostOf.LogData);
        }
        catch (OverflowException)
        {
            throw new SubprotocolException("Logs gas overflows long for a single receipt");
        }
    }
}
