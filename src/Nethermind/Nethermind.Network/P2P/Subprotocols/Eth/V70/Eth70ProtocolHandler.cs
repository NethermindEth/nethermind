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
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Evm;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V70;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-7975 - partial block receipt lists.
/// </summary>
public class Eth70ProtocolHandler : Eth69ProtocolHandler
{
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
        _receiptsRequests70 = new MessageDictionary<GetReceiptsMessage70, ReceiptsMessage70>(Send);
    }

    public override string Name => "eth70";

    public override byte ProtocolVersion => EthVersions.Eth70;

    public override void HandleMessage(ZeroPacket message)
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
                base.HandleMessage(message);
                break;
        }
    }

    private void Handle(ReceiptsMessage70 msg, long size) => _receiptsRequests70.Handle(msg.RequestId, msg, size);

    private async Task<ReceiptsMessage70> Handle(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        ReceiptsResponse response = await FulfillReceiptsRequest(getReceiptsMessage, cancellationToken);
        return new ReceiptsMessage70(getReceiptsMessage.RequestId, new(response.TxReceipts), response.LastBlockIncomplete);
    }

    private Task<ReceiptsResponse> FulfillReceiptsRequest(GetReceiptsMessage70 getReceiptsMessage, CancellationToken cancellationToken)
    {
        ArrayPoolList<TxReceipt[]> txReceipts = new(getReceiptsMessage.EthMessage.Hashes.Count);
        bool lastBlockIncomplete = false;

        ulong sizeEstimate = 0;
        for (int blockIndex = 0; blockIndex < getReceiptsMessage.EthMessage.Hashes.Count; blockIndex++)
        {
            TxReceipt[] receipts = SyncServer.GetReceipts(getReceiptsMessage.EthMessage.Hashes[blockIndex]);
            int startIndex = blockIndex == 0 ? checked((int)getReceiptsMessage.FirstBlockReceiptIndex) : 0;

            if (receipts.Length == 0)
            {
                if (startIndex != 0)
                {
                    txReceipts.Dispose();
                    throw new SubprotocolException($"Invalid firstBlockReceiptIndex {startIndex} for empty receipts block");
                }

                txReceipts.Add([]);
                continue;
            }

            if (startIndex < 0 || startIndex >= receipts.Length)
            {
                txReceipts.Dispose();
                throw new SubprotocolException($"Invalid firstBlockReceiptIndex {startIndex} for block receipts length {receipts.Length}");
            }

            int taken = 0;

            for (int receiptIndex = startIndex; receiptIndex < receipts.Length; receiptIndex++)
            {
                taken++;
                sizeEstimate += MessageSizeEstimator.EstimateSize(receipts[receiptIndex]);

                if (sizeEstimate > SoftOutgoingMessageSizeLimit || cancellationToken.IsCancellationRequested)
                {
                    lastBlockIncomplete = receiptIndex < receipts.Length - 1 || cancellationToken.IsCancellationRequested;
                    break;
                }
            }

            TxReceipt[] truncated = new TxReceipt[taken];
            Array.Copy(receipts, startIndex, truncated, 0, taken);
            txReceipts.Add(truncated);

            if (lastBlockIncomplete || sizeEstimate > SoftOutgoingMessageSizeLimit || cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return Task.FromResult(new ReceiptsResponse(txReceipts, lastBlockIncomplete));
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
            async clampedHashes => await SendGetReceiptsWithPaging(clampedHashes, token));
    }

    private async Task<(IOwnedReadOnlyList<TxReceipt[]>, long)> SendGetReceiptsWithPaging(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        ArrayPoolList<TxReceipt[]> aggregated = new(blockHashes.Count);
        int blockIndex = 0;
        int firstBlockReceiptIndex = 0;
        ArrayPoolList<TxReceipt>? partialReceipts = null;
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

                    if (response.EthMessage.TxReceipts.Count == 0)
                    {
                        throw new SubprotocolException("Received empty Receipts payload in eth/70 response");
                    }

                    if (response.EthMessage.TxReceipts.Count > blockHashes.Count - blockIndex)
                    {
                        throw new SubprotocolException("Received more receipts than requested in eth/70 response");
                    }

                    if (response.LastBlockIncomplete && size < SoftOutgoingMessageSizeLimit / 4)
                    {
                        throw new SubprotocolException($"Received partial receipts response below minimum size ({size} bytes < {SoftOutgoingMessageSizeLimit / 4} bytes)");
                    }

                    IOwnedReadOnlyList<TxReceipt[]?> txReceipts = response.EthMessage.TxReceipts;

                    for (int i = 0; i < txReceipts.Count; i++)
                    {
                        bool isFirst = i == 0;
                        bool isLast = i == txReceipts.Count - 1;
                        TxReceipt[] blockReceipts = txReceipts[i];

                        if (isFirst && firstBlockReceiptIndex > 0)
                        {
                            if (partialReceipts is null)
                            {
                                throw new SubprotocolException("Unexpected receipts continuation without pending state");
                            }

                            partialReceipts.AddRange(blockReceipts);
                            ValidateBlockReceipts(blockReceipts, expectedGasUsed[blockIndex], firstBlockReceiptIndex, isLast && !response.LastBlockIncomplete);

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
                                ValidateBlockReceipts(completed, expectedGasUsed[blockIndex], 0, true);
                                aggregated.Add(completed);
                                partialReceipts.Dispose();
                                partialReceipts = null;
                                blockIndex++;
                                firstBlockReceiptIndex = 0;
                            }

                            continue;
                        }

                        if (response.LastBlockIncomplete && isLast)
                        {
                            if (blockReceipts.Length == 0)
                            {
                                throw new SubprotocolException("Peer returned no progress for partial receipts");
                            }

                            ValidateBlockReceipts(blockReceipts, expectedGasUsed[blockIndex], firstBlockReceiptIndex, false);
                            partialReceipts = new ArrayPoolList<TxReceipt>(blockReceipts.Length + firstBlockReceiptIndex);
                            partialReceipts.AddRange(blockReceipts);
                            firstBlockReceiptIndex = partialReceipts.Count;
                        }
                        else
                        {
                            ValidateBlockReceipts(blockReceipts, expectedGasUsed[blockIndex], firstBlockReceiptIndex, true);
                            aggregated.Add(blockReceipts);
                            blockIndex++;
                            firstBlockReceiptIndex = 0;
                        }
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

    private static GetReceiptsMessage70 BuildRequest(IReadOnlyList<Hash256> blockHashes, int startIndex, int firstReceiptIndex)
    {
        ArrayPoolList<Hash256> remainingHashes = new(blockHashes.Count - startIndex);
        for (int i = startIndex; i < blockHashes.Count; i++)
        {
            remainingHashes.Add(blockHashes[i]);
        }

        return new GetReceiptsMessage70
        {
            FirstBlockReceiptIndex = firstReceiptIndex,
            EthMessage = new(remainingHashes)
        };
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

    private static void ValidateBlockReceipts(TxReceipt[] blockReceipts, long expectedGasUsed, int firstReceiptIndex, bool isCompleteSegment)
    {
        if (blockReceipts.Length == 0)
        {
            throw new SubprotocolException("Empty receipt block payload");
        }

        long prevCumulative = firstReceiptIndex == 0 ? 0 : blockReceipts[0].GasUsedTotal;
        for (int i = 1; i < blockReceipts.Length; i++)
        {
            if (blockReceipts[i].GasUsedTotal < prevCumulative)
            {
                throw new SubprotocolException("Cumulative gas decreased within block receipts");
            }

            prevCumulative = blockReceipts[i].GasUsedTotal;
        }

        long blockGasUsed = blockReceipts[^1].GasUsedTotal;
        if (blockGasUsed <= 0)
        {
            throw new SubprotocolException("Invalid block gas used in receipts");
        }

        if (expectedGasUsed > 0 && blockGasUsed > expectedGasUsed)
        {
            throw new SubprotocolException("Block gas used exceeds header value");
        }

        long totalTxCount = checked(firstReceiptIndex + blockReceipts.Length);
        long intrinsicLowerBound = checked(totalTxCount * GasCostOf.Transaction);
        if (intrinsicLowerBound > blockGasUsed)
        {
            throw new SubprotocolException("Intrinsic gas lower bound exceeds block gas used");
        }

        long gasUpperBound = expectedGasUsed > 0 ? expectedGasUsed : blockGasUsed;

        if (isCompleteSegment)
        {
            long logsGas = 0;
            foreach (TxReceipt receipt in blockReceipts)
            {
                if (receipt.Logs is null)
                {
                    continue;
                }

                foreach (LogEntry log in receipt.Logs)
                {
                    int topics = log.Topics?.Length ?? 0;
                    int dataLength = log.Data?.Length ?? 0;
                    logsGas = checked(logsGas + GasCostOf.Log + topics * GasCostOf.LogTopic + dataLength * GasCostOf.LogData);
                }
            }

            if (logsGas > gasUpperBound)
            {
                throw new SubprotocolException("Logs gas exceeds block gas used");
            }

            if (expectedGasUsed > 0 && blockGasUsed != expectedGasUsed)
            {
                throw new SubprotocolException("Block gas used mismatch between receipts and header");
            }
        }
    }
}
